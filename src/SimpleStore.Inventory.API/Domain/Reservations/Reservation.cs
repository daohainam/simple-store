using SimpleStore.Inventory.API.Domain.Reservations.Events;
using SimpleStore.Inventory.API.Domain.Shared;

namespace SimpleStore.Inventory.API.Domain.Reservations;

// DDD aggregate root: a temporary stock hold for an order (stock OUT).
//
// A Reservation is reserved once (StockReservedV1 decrements stock_levels.OnHand immediately) and
// can then be released. v12 adds Cancel (release the hold when payment fails) plus the matching
// StockReservationCancelledV1 event — the projector adds the held quantity back to OnHand. Commit
// (convert to a delivery note when the order ships) is still future work.
//
// At the store level, the initial reserve is enforced with StreamState.NoStream (a saga retry with
// the same ReservationId collapses onto the same stream and returns 409); the cancel appends with
// the expected stream revision so a redelivered cancel also collapses to a no-op.
public sealed class Reservation
{
    public Guid Id { get; private set; }
    public Guid CorrelationId { get; private set; }
    public int OrderId { get; private set; }
    public DateTimeOffset ReservedAt { get; private set; }
    public IReadOnlyList<InventoryLine> Lines => _lines;

    // True once the reservation has been released (StockReservationCancelledV1 applied). Lets the
    // CancelReservationHandler treat a redelivered cancel request as an idempotent no-op.
    public bool IsCancelled => _cancelled;

    private readonly List<InventoryLine> _lines = [];
    private readonly List<IInventoryDomainEvent> _uncommitted = [];
    private bool _reserved;
    private bool _cancelled;

    public IReadOnlyList<IInventoryDomainEvent> UncommittedEvents => _uncommitted;
    public void MarkEventsCommitted() => _uncommitted.Clear();

    private Reservation() { }

    public static Reservation Reserve(
        Guid reservationId,
        Guid correlationId,
        int orderId,
        IReadOnlyList<InventoryLine> lines,
        DateTimeOffset now)
    {
        if (reservationId == Guid.Empty)
            throw new DomainException("Reservation id must be a non-empty Guid.");
        if (lines is null || lines.Count == 0)
            throw new DomainException("A reservation must have at least one line.");

        var deduped = new HashSet<int>();
        foreach (var line in lines)
        {
            if (!deduped.Add(line.ProductId))
                throw new DomainException(
                    $"Duplicate ProductId {line.ProductId} on the same reservation. " +
                    "Collapse into a single line.");
        }

        var evt = new StockReservedV1
        {
            NoteId = reservationId,
            CorrelationId = correlationId,
            OrderId = orderId,
            ReservedAt = now,
            Lines = lines
                .Select(l => new StockReservedV1.LineData
                {
                    ProductId = l.ProductId,
                    Quantity = l.Quantity
                })
                .ToList()
        };

        var reservation = new Reservation();
        reservation.Apply(evt);
        reservation._uncommitted.Add(evt);
        return reservation;
    }

    public static Reservation Rehydrate(IEnumerable<IInventoryDomainEvent> events)
    {
        var reservation = new Reservation();
        foreach (var evt in events) reservation.Apply(evt);
        if (!reservation._reserved)
            throw new DomainException("Reservation stream did not contain a Reserved event.");
        return reservation;
    }

    // Releases the hold. Emits StockReservationCancelledV1 carrying the reserved lines so the
    // projector knows how much stock to return to OnHand. Guarded so a double-cancel is rejected;
    // callers that may redeliver should check IsCancelled (after Rehydrate) first.
    public void Cancel(DateTimeOffset now)
    {
        if (!_reserved)
            throw new DomainException("Cannot cancel a reservation that was never reserved.");
        if (_cancelled)
            throw new DomainException("Reservation has already been cancelled.");

        var evt = new StockReservationCancelledV1
        {
            NoteId = Id,
            CorrelationId = CorrelationId,
            OrderId = OrderId,
            CancelledAt = now,
            Lines = _lines
                .Select(l => new StockReservationCancelledV1.LineData
                {
                    ProductId = l.ProductId,
                    Quantity = l.Quantity
                })
                .ToList()
        };

        Apply(evt);
        _uncommitted.Add(evt);
    }

    private void Apply(IInventoryDomainEvent @event)
    {
        switch (@event)
        {
            case StockReservedV1 reserved:
                if (_reserved)
                    throw new DomainException("Reservation has already been reserved.");
                Id = reserved.NoteId;
                CorrelationId = reserved.CorrelationId;
                OrderId = reserved.OrderId;
                ReservedAt = reserved.ReservedAt;
                _lines.AddRange(reserved.Lines.Select(l => new InventoryLine(l.ProductId, l.Quantity)));
                _reserved = true;
                break;
            case StockReservationCancelledV1:
                if (!_reserved)
                    throw new DomainException("Cannot cancel a reservation that was never reserved.");
                if (_cancelled)
                    throw new DomainException("Reservation has already been cancelled.");
                _cancelled = true;
                break;
            default:
                throw new DomainException(
                    $"Reservation cannot apply event of type {@event.GetType().Name}.");
        }
    }
}
