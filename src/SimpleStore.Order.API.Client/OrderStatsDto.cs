namespace SimpleStore.Order.API.Client;

public class OrderStatsDto
{
    public int TotalCount { get; set; }
    public int PendingCount { get; set; }
    public int CompletedCount { get; set; }
    public decimal TotalRevenue { get; set; }
}
