using System.ComponentModel.DataAnnotations;

namespace SimpleStore.Order.API.Models;

public class Order
{
    public int Id { get; set; }
    public Guid CorrelationId { get; set; }
    [Required, MaxLength(450)]
    public string UserId { get; set; } = string.Empty;
    public DateTime OrderDate { get; set; }
    public decimal TotalAmount { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.Pending;
    [Required, MaxLength(500)]
    public string ShippingAddress { get; set; } = string.Empty;
    public ICollection<OrderItem> Items { get; set; } = new List<OrderItem>();
}
