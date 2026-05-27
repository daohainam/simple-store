using Microsoft.EntityFrameworkCore;
using SimpleStore.Inventory.API.Data;
using SimpleStore.Inventory.API.Domain.DeliveryNotes.Events;
using SimpleStore.Inventory.API.Domain.ReceiptNotes.Events;
using SimpleStore.Inventory.API.Domain.Reservations.Events;
using SimpleStore.Inventory.API.EventStore;
using SimpleStore.Inventory.API.Projections.Checkpoints;

namespace SimpleStore.Inventory.API.Projections;

// CQRS read-side ASYNC projector.
//
// COLD START / FULL REPLAY: if projection_checkpoints is empty (e.g. you wiped
// the read DB), the subscription starts from FromAll.Start and rebuilds every
// table from the event store. That IS the documented recovery procedure for
// any breaking read-model change. The event store is the source of truth;
// these tables are caches.
//
// SINGLE REPLICA: there is no lease here. Running two replicas would race
// for the cursor. Future-work: switch to a KurrentDB persistent subscription
// with a consumer group, which solves multi-replica natively.
//
// IDEMPOTENCY: read-model writes AND checkpoint update happen in one Postgres
// transaction. A crash after appending to KurrentDB but before committing the
// transaction means the projector re-receives the event on restart and the
// per-event "have I seen this NoteId already?" guard in InventoryProjector
// makes the re-apply a no-op.
public sealed class InventoryProjectionService : BackgroundService
{
    public const string ProjectionName = "inventory-read-model";

    private static readonly string[] StreamPrefixes = ["deliveryNote-", "receiptNote-", "reservation-"];

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly ILogger<InventoryProjectionService> _log;

    public InventoryProjectionService(
        IServiceScopeFactory scopes,
        TimeProvider clock,
        ILogger<InventoryProjectionService> log)
    {
        _scopes = scopes;
        _clock = clock;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give KurrentDB a moment to become reachable on first cold boot.
        // The toolkit's WaitFor returns when the container is "running"; the
        // gRPC endpoint may take a beat to accept connections.
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        EventStorePosition? checkpoint;
        await using (var scope = _scopes.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<CheckpointStore>();
            checkpoint = await store.LoadAsync(ProjectionName, stoppingToken);
        }

        _log.LogInformation(
            "Inventory projector starting at {Position}.",
            checkpoint?.ToString() ?? "FromAll.Start (cold start / full replay)");

        var eventStore = _scopes.CreateScope().ServiceProvider.GetRequiredService<IEventStore>();

        await foreach (var envelope in eventStore
            .SubscribeAllAsync(StreamPrefixes, checkpoint, stoppingToken)
            .WithCancellation(stoppingToken))
        {
            try
            {
                await ApplyOneAsync(envelope, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _log.LogError(ex,
                    "Failed to project event {Type} from stream {Stream}. Stopping projector to avoid silent data loss.",
                    envelope.Type, envelope.StreamName);
                throw;
            }
        }
    }

    private async Task ApplyOneAsync(EventEnvelope envelope, CancellationToken ct)
    {
        if (envelope.DomainEvent is null)
        {
            // Unknown event type — checkpoint past it but project nothing.
            // Lets us roll out v2 schemas without crashing the projector if
            // an older replica reads a newer event.
            _log.LogWarning(
                "Projector skipped unknown event type {EventType} at position {Position} in stream {Stream}.",
                envelope.Type, envelope.Position?.ToString() ?? "unknown", envelope.StreamName);
            await CheckpointOnlyAsync(envelope, ct);
            return;
        }

        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryReadDbContext>();
        var projector = scope.ServiceProvider.GetRequiredService<InventoryProjector>();
        var checkpoints = scope.ServiceProvider.GetRequiredService<CheckpointStore>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        switch (envelope.DomainEvent)
        {
            case DeliveryNoteIssuedV1 issued:
                await projector.ApplyDeliveryNoteIssuedAsync(issued, envelope.IsLive, ct);
                break;
            case ReceiptNoteRecordedV1 recorded:
                await projector.ApplyReceiptNoteRecordedAsync(recorded, envelope.IsLive, ct);
                break;
            case StockReservedV1 reserved:
                await projector.ApplyStockReservedAsync(reserved, envelope.IsLive, ct);
                break;
            default:
                _log.LogWarning("Unhandled domain event {Type}.", envelope.DomainEvent.GetType().Name);
                break;
        }

        if (envelope.Position is { } pos)
        {
            await checkpoints.UpsertAsync(ProjectionName, pos, _clock.GetUtcNow(), ct);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task CheckpointOnlyAsync(EventEnvelope envelope, CancellationToken ct)
    {
        if (envelope.Position is not { } pos) return;
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryReadDbContext>();
        var checkpoints = scope.ServiceProvider.GetRequiredService<CheckpointStore>();
        await checkpoints.UpsertAsync(ProjectionName, pos, _clock.GetUtcNow(), ct);
        await db.SaveChangesAsync(ct);
    }
}
