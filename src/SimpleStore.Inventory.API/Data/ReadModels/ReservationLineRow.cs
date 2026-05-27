namespace SimpleStore.Inventory.API.Data.ReadModels;

// One row per (reservation, product). Composite primary key (ReservationId, LineNumber)
// communicates that lines have no independent identity outside their reservation.
// ProductId is a SOFT REFERENCE to Catalog.Products — no FK, no JOIN.
public class ReservationLineRow
{
    public Guid ReservationId { get; set; }
    public int LineNumber { get; set; }
    public int ProductId { get; set; }
    public int Quantity { get; set; }
}
