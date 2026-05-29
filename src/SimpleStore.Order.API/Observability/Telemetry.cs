using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SimpleStore.Order.API.Observability;

// v10: per-service ActivitySource + Meter convention. ServiceDefaults' wildcard registration
// (AddSource("SimpleStore.*") / AddMeter("SimpleStore.*")) picks these up automatically — no
// per-service registration call needed.
internal static class Telemetry
{
    public const string SourceName = "SimpleStore.Order";

    public static readonly ActivitySource Source = new(SourceName);
    public static readonly Meter Meter = new(SourceName);

    // Business counters. Tag every increment with the saga CorrelationId so they can be split
    // by tenant/user/cohort in the dashboard. Cardinality is bounded by the saga lifetime.
    public static readonly Counter<long> OrdersSubmitted = Meter.CreateCounter<long>(
        "simplestore.orders.submitted",
        description: "Count of orders created via POST /api/order/orders.");

    public static readonly Counter<long> OrdersConfirmed = Meter.CreateCounter<long>(
        "simplestore.orders.confirmed",
        description: "Count of orders moved to Confirmed by the saga's OrderConfirmedConsumer.");

    public static readonly Counter<long> OrdersCancelled = Meter.CreateCounter<long>(
        "simplestore.orders.cancelled",
        description: "Count of orders moved to Cancelled by the saga's OrderCancelledConsumer.");
}
