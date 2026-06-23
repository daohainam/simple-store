using SimpleStore.Inventory.API.Domain;
using SimpleStore.Inventory.API.Domain.Reservations;
using SimpleStore.Inventory.API.EventStore;
using SimpleStore.Inventory.API.Observability;

namespace SimpleStore.Inventory.API.Application.Reservations;

// CQRS write-side handler for the checkout saga's release-stock (compensation) step. It rehydrates
// the reservation aggregate from its KurrentDB stream, appends StockReservationCancelledV1, and
// stops there: the async projector adds the held quantity back to stock_levels.OnHand and publishes
// StockReservationCancelledEventV1 (gated on isLive), exactly as the reserve path publishes
// StockReservedEventV1 from the projector. This handler touches no Postgres — no availability check
// is needed when releasing a hold.
//
// IDEMPOTENCY: a redelivered cancel is a no-op. After rehydration we check IsCancelled and bail; and
// the append uses the expected stream revision so a concurrent second cancel hits
// ConcurrencyConflictException, which we also treat as success.
public sealed class CancelReservationHandler
{
    private readonly IEventStore _eventStore;
    private readonly TimeProvider _clock;
    private readonly ILogger<CancelReservationHandler> _log;

    public CancelReservationHandler(
        IEventStore eventStore,
        TimeProvider clock,
        ILogger<CancelReservationHandler> log)
    {
        _eventStore = eventStore;
        _clock = clock;
        _log = log;
    }

    public async Task HandleAsync(CancelReservationCommand cmd, CancellationToken ct)
    {
        var streamName = $"reservation-{cmd.ReservationId}";

        var events = new List<IInventoryDomainEvent>();
        await foreach (var envelope in _eventStore.ReadStreamAsync(streamName, ct))
        {
            if (envelope.DomainEvent is not null)
                events.Add(envelope.DomainEvent);
        }

        if (events.Count == 0)
        {
            // The saga only requests a cancel AFTER it received StockReservedEventV1, which the
            // projector publishes only after StockReservedV1 is appended — so in the normal flow the
            // stream exists. A missing stream means a wrong id or a wiped store; nothing to release.
            _log.LogWarning(
                "Cancel requested for unknown reservation {ReservationId} (order {OrderId}) — nothing to release.",
                cmd.ReservationId, cmd.OrderId);
            return;
        }

        var reservation = Reservation.Rehydrate(events);
        if (reservation.IsCancelled)
        {
            _log.LogInformation(
                "Reservation {ReservationId} already released — treating redelivery as success.", cmd.ReservationId);
            return;
        }

        reservation.Cancel(_clock.GetUtcNow());

        try
        {
            await _eventStore.AppendAsync(
                streamName,
                reservation.UncommittedEvents,
                new AppendCondition.StreamRevision((ulong)(events.Count - 1)),
                ct);
        }
        catch (ConcurrencyConflictException)
        {
            // A concurrent delivery appended the cancel first. The projector will restore stock and
            // publish StockReservationCancelledEventV1, so the saga still gets its answer. No-op success.
            _log.LogInformation(
                "Reservation {ReservationId} cancel raced — already appended by another delivery.", cmd.ReservationId);
            return;
        }

        Telemetry.ReservationsCancelled.Add(1);
        _log.LogInformation(
            "Reservation {ReservationId} for order {OrderId} released (stock returned to OnHand).",
            cmd.ReservationId, cmd.OrderId);
    }
}
