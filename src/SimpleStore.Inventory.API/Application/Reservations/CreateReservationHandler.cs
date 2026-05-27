using MassTransit;
using Microsoft.EntityFrameworkCore;
using SimpleStore.Contracts;
using SimpleStore.Inventory.API.Data;
using SimpleStore.Inventory.API.Domain.Reservations;
using SimpleStore.Inventory.API.Domain.Shared;
using SimpleStore.Inventory.API.EventStore;

namespace SimpleStore.Inventory.API.Application.Reservations;

// CQRS write-side handler for the checkout saga's reserve-stock step. Unlike the delivery/receipt
// handlers, this one has two outcomes:
//
//   SUCCESS — enough stock on hand: append StockReservedV1 to KurrentDB. The async projector then
//             decrements stock_levels.OnHand and publishes StockReservedEvent + StockLevelChangedEvent.
//             This handler publishes NOTHING on success — the projector owns that.
//
//   FAILURE — insufficient stock: append NOTHING to the event store (a rejected command emits no
//             domain event, DDD-style) and publish StockReservationFailedEvent directly through the
//             MassTransit EF bus outbox. The saga consumes it and cancels the order.
//
// Concurrency: we SELECT ... FOR UPDATE the stock_levels rows so concurrent reservation handlers
// serialize per product. NOTE the documented race window — OnHand is decremented by the async
// projector, not here, so two reservations that arrive before the projector catches up can both
// pass the availability check and oversell. Acceptable for the sample; see docs/checkout-saga.md §10.2.
public sealed class CreateReservationHandler
{
    private readonly InventoryReadDbContext _readDb;
    private readonly IEventStore _eventStore;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly TimeProvider _clock;
    private readonly ILogger<CreateReservationHandler> _log;

    public CreateReservationHandler(
        InventoryReadDbContext readDb,
        IEventStore eventStore,
        IPublishEndpoint publishEndpoint,
        TimeProvider clock,
        ILogger<CreateReservationHandler> log)
    {
        _readDb = readDb;
        _eventStore = eventStore;
        _publishEndpoint = publishEndpoint;
        _clock = clock;
        _log = log;
    }

    private const int MaxReservationLines = 100;

    public async Task HandleAsync(CreateReservationCommand cmd, CancellationToken ct)
    {
        if (cmd.Lines is null || cmd.Lines.Count == 0)
            throw new DomainException("A reservation must have at least one line.");
        if (cmd.Lines.Count > MaxReservationLines)
            throw new DomainException($"A reservation may not exceed {MaxReservationLines} lines.");
        var invalidLine = cmd.Lines.FirstOrDefault(l => l.Quantity <= 0);
        if (invalidLine is not null)
            throw new DomainException($"Reservation line for product {invalidLine.ProductId} has invalid quantity {invalidLine.Quantity}.");

        var ids = cmd.Lines.Select(l => l.ProductId).Distinct().ToArray();

        // Explicit transaction: the FOR UPDATE lock is held until we commit, serializing concurrent
        // reservation handlers. The publish on the failure path rides the same transaction's bus
        // outbox flush — identical pattern to OrderService.CreateOrderAsync.
        await using var tx = await _readDb.Database.BeginTransactionAsync(ct);

        var levels = await _readDb.StockLevels
            .FromSqlInterpolated($"SELECT * FROM stock_levels WHERE \"ProductId\" = ANY({ids}) FOR UPDATE")
            .ToDictionaryAsync(s => s.ProductId, ct);

        var shortages = new List<ShortageLine>();
        foreach (var line in cmd.Lines)
        {
            var onHand = levels.TryGetValue(line.ProductId, out var lvl) ? lvl.OnHand : 0;
            if (onHand < line.Quantity)
                shortages.Add(new ShortageLine
                {
                    ProductId = line.ProductId,
                    Requested = line.Quantity,
                    Available = onHand
                });
        }

        if (shortages.Count > 0)
        {
            await _publishEndpoint.Publish(new StockReservationFailedEvent
            {
                CorrelationId = cmd.CorrelationId,
                ReservationId = cmd.ReservationId,
                OrderId = cmd.OrderId,
                Reason = "InsufficientStock",
                ShortageLines = shortages,
                FailedAt = _clock.GetUtcNow()
            }, ct);
            await _readDb.SaveChangesAsync(ct); // flush the bus outbox in this transaction
            await tx.CommitAsync(ct);
            _log.LogInformation(
                "Reservation {ReservationId} for order {OrderId} rejected — insufficient stock on {Count} line(s).",
                cmd.ReservationId, cmd.OrderId, shortages.Count);
            return;
        }

        var domainLines = cmd.Lines.Select(l => new InventoryLine(l.ProductId, l.Quantity)).ToList();
        var reservation = Reservation.Reserve(
            cmd.ReservationId, cmd.CorrelationId, cmd.OrderId, domainLines, _clock.GetUtcNow());

        try
        {
            await _eventStore.AppendAsync(
                $"reservation-{reservation.Id}", reservation.UncommittedEvents, AppendCondition.NoStream, ct);
        }
        catch (ConcurrencyConflictException)
        {
            // Saga retry: this ReservationId already exists in the event store. The original
            // StockReservedV1 was (or will be) projected and StockReservedEvent published, so the
            // saga already got (or will get) its answer. Treat the retry as a no-op success.
            await tx.CommitAsync(ct);
            _log.LogInformation(
                "Reservation {ReservationId} already exists — treating redelivery as success.", cmd.ReservationId);
            return;
        }

        await _readDb.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
