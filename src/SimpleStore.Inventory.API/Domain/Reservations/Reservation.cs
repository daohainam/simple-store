using SimpleStore.Inventory.API.Domain.Reservations.Events;
using SimpleStore.Inventory.API.Domain.Shared;

namespace SimpleStore.Inventory.API.Domain.Reservations;

// DDD aggregate root: a temporary stock hold for an order (stock OUT).
//
// In v8 a Reservation is single-shot like DeliveryNote / ReceiptNote: it can only be reserved,
// and the StockReservedV1 projection decrements stock_levels.OnHand immediately. v9 will add
// Commit (convert to a delivery note when the order ships) and Cancel (release the hold when an
// order is cancelled) behaviors plus the matching domain events.
//
// At the store level, single-issuance is enforced by appending with StreamState.NoStream — a
// saga retry with the same ReservationId collapses onto the same stream and returns 409.
public sealed class Reservation
{
    public Guid Id { get; private set; }
    public Guid CorrelationId { get; private set; }
    public int OrderId { get; private set; }
    public DateTimeOffset ReservedAt { get; private set; }
    public IReadOnlyList<InventoryLine> Lines => _lines;

    private readonly List<InventoryLine> _lines = [];
    private readonly List<IInventoryDomainEvent> _uncommitted = [];
    private bool _reserved;

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
            default:
                throw new DomainException(
                    $"Reservation cannot apply event of type {@event.GetType().Name}.");
        }
    }
}
