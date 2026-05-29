using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using SimpleStore.Inventory.API.Data;
using SimpleStore.Inventory.API.Domain.DeliveryNotes.Events;
using SimpleStore.Inventory.API.Domain.ReceiptNotes.Events;
using SimpleStore.Inventory.API.Domain.Reservations.Events;
using SimpleStore.Inventory.API.EventStore;
using SimpleStore.Inventory.API.Observability;
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
//
// RESILIENCE (v9): the subscription is wrapped in an outer reconnect loop with
// exponential backoff (1s → 30s). Any exception from KurrentDB (network drop,
// gRPC deadline, server restart) or from the projection transaction is caught,
// logged, and triggers a reconnect after the current backoff — the checkpoint
// is reloaded from Postgres so we resume exactly where we left off. The
// per-event apply runs inside EF Core's IExecutionStrategy so transient
// Postgres errors retry the projection transaction without dropping the
// subscription.
public sealed class InventoryProjectionService : BackgroundService
{
    public const string ProjectionName = "inventory-read-model";

    private static readonly string[] StreamPrefixes = ["deliveryNote-", "receiptNote-", "reservation-"];
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly ILogger<InventoryProjectionService> _log;

    // v10: in-memory cursors for the projector-lag gauge. Updated as events flow through:
    //   _lastSeenTailCommit  — set when a new envelope arrives from the subscription
    //   _lastAppliedCommit   — set after a successful Apply transaction commits
    // The gauge callback returns the delta. Cheap (two volatile reads, no I/O); reflects
    // "events behind" approximately, in commit-log byte units (see Telemetry.cs notes).
    private long _lastSeenTailCommit;
    private long _lastAppliedCommit;

    public InventoryProjectionService(
        IServiceScopeFactory scopes,
        TimeProvider clock,
        ILogger<InventoryProjectionService> log)
    {
        _scopes = scopes;
        _clock = clock;
        _log = log;

        // v10: wire the gauge callback. Telemetry holds a Func<long>; the OTel pipeline reads
        // it at metric collection time. No allocation, no I/O — just two volatile loads.
        Telemetry.SetProjectorLagProvider(() =>
        {
            var tail = Volatile.Read(ref _lastSeenTailCommit);
            var applied = Volatile.Read(ref _lastAppliedCommit);
            return tail > applied ? tail - applied : 0L;
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give KurrentDB a moment to become reachable on first cold boot.
        // The toolkit's WaitFor returns when the container is "running"; the
        // gRPC endpoint may take a beat to accept connections.
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        var backoff = MinBackoff;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSubscriptionLoopAsync(stoppingToken);
                // Clean exit (subscription completed without throwing). Reset backoff before reconnecting.
                backoff = MinBackoff;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "Inventory projector subscription dropped; reconnecting in {BackoffSeconds}s.",
                    backoff.TotalSeconds);
                try
                {
                    await Task.Delay(backoff, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                backoff = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, backoff.TotalSeconds * 2));
            }
        }
    }

    private async Task RunSubscriptionLoopAsync(CancellationToken stoppingToken)
    {
        EventStorePosition? checkpoint;
        await using (var scope = _scopes.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<CheckpointStore>();
            checkpoint = await store.LoadAsync(ProjectionName, stoppingToken);
        }

        _log.LogInformation(
            "Inventory projector starting at {Position}.",
            checkpoint?.ToString() ?? "FromAll.Start (cold start / full replay)");

        using var eventStoreScope = _scopes.CreateScope();
        var eventStore = eventStoreScope.ServiceProvider.GetRequiredService<IEventStore>();

        await foreach (var envelope in eventStore
            .SubscribeAllAsync(StreamPrefixes, checkpoint, stoppingToken)
            .WithCancellation(stoppingToken))
        {
            // v10: track tail as events arrive — the gauge callback reads this to compute lag.
            if (envelope.Position is { } seen)
            {
                Volatile.Write(ref _lastSeenTailCommit, unchecked((long)seen.CommitPosition));
            }

            await ApplyOneAsync(envelope, stoppingToken);
        }
    }

    private async Task ApplyOneAsync(EventEnvelope envelope, CancellationToken ct)
    {
        // v10: per-event activity span. The MassTransit publish (from InventoryProjector) and the EF
        // execution-strategy transaction become nested children of this span, so the dashboard's
        // trace view shows one parent per projected event with the read-write + integration-publish
        // as a clear unit of work. Tag with event_type + is_live + stream for filtering.
        using var activity = Telemetry.Source.StartActivity(
            "InventoryProjector.Apply",
            ActivityKind.Consumer);
        activity?.SetTag("inventory.event_type", envelope.DomainEvent?.GetType().Name ?? envelope.Type);
        activity?.SetTag("inventory.is_live", envelope.IsLive);
        activity?.SetTag("inventory.stream", envelope.StreamName);
        if (envelope.Position is { } pos0)
        {
            activity?.SetTag("inventory.position.commit", unchecked((long)pos0.CommitPosition));
        }

        if (envelope.DomainEvent is null)
        {
            // Unknown event type — checkpoint past it but project nothing.
            // Lets us roll out v2 schemas without crashing the projector if
            // an older replica reads a newer event.
            _log.LogWarning(
                "Projector skipped unknown event type {EventType} at position {Position} in stream {Stream}.",
                envelope.Type, envelope.Position?.ToString() ?? "unknown", envelope.StreamName);
            activity?.SetStatus(ActivityStatusCode.Ok, "skipped-unknown-event");
            await CheckpointOnlyAsync(envelope, ct);
            return;
        }

        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryReadDbContext>();
        var projector = scope.ServiceProvider.GetRequiredService<InventoryProjector>();
        var checkpoints = scope.ServiceProvider.GetRequiredService<CheckpointStore>();

        // v9: wrapped in IExecutionStrategy so the read-model write + checkpoint upsert can retry on
        // a transient Postgres error without dropping the KurrentDB subscription. Re-applies are
        // idempotent: InventoryProjector's per-aggregate "have I seen this Id already?" guards make
        // every projection a no-op on the second pass, and the checkpoint upsert is monotonic.
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
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
        });

        // v10: update applied-position AFTER the transaction commits so the gauge reflects
        // "successfully projected" rather than "received but not yet durable".
        if (envelope.Position is { } applied)
        {
            Volatile.Write(ref _lastAppliedCommit, unchecked((long)applied.CommitPosition));
        }
    }

    private async Task CheckpointOnlyAsync(EventEnvelope envelope, CancellationToken ct)
    {
        if (envelope.Position is not { } pos) return;
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryReadDbContext>();
        var checkpoints = scope.ServiceProvider.GetRequiredService<CheckpointStore>();

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await checkpoints.UpsertAsync(ProjectionName, pos, _clock.GetUtcNow(), ct);
            await db.SaveChangesAsync(ct);
        });

        // v10: advance the gauge cursor — even when we project nothing, the checkpoint
        // moves and the projector is "caught up" past this event.
        Volatile.Write(ref _lastAppliedCommit, unchecked((long)pos.CommitPosition));
    }
}
