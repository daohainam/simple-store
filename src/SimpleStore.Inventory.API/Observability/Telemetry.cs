using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SimpleStore.Inventory.API.Observability;

internal static class Telemetry
{
    public const string SourceName = "SimpleStore.Inventory";

    public static readonly ActivitySource Source = new(SourceName);
    public static readonly Meter Meter = new(SourceName);

    public static readonly Counter<long> ReservationsRequested = Meter.CreateCounter<long>(
        "simplestore.reservations.requested",
        description: "Count of ReserveStockRequestedEvent messages consumed.");

    public static readonly Counter<long> ReservationsSucceeded = Meter.CreateCounter<long>(
        "simplestore.reservations.succeeded",
        description: "Count of reservations that passed the stock check and appended StockReservedV1.");

    public static readonly Counter<long> ReservationsFailed = Meter.CreateCounter<long>(
        "simplestore.reservations.failed",
        description: "Count of reservations that failed stock check and published StockReservationFailedEvent.");

    // Projector lag is observed (callback-based) instead of recorded, because the value is computed
    // by subtracting the projector's last applied checkpoint from KurrentDB's tail position — both
    // are read on demand. The number is the commit-log POSITION delta (bytes), not an event count;
    // KurrentDB's $all is indexed by byte position, so this is what's cheap to measure. For our
    // payload sizes the delta is monotonically related to "events behind" and that's the operational
    // signal we want — "is the projector keeping up?"
    //
    // InventoryProjectionService wires the provider at startup; until it does, the gauge reports 0
    // (steady-state assumption is safer than a stale "unknown" value).
    private static Func<long> _projectorLagProvider = () => 0L;

    public static readonly ObservableGauge<long> ProjectorLag = Meter.CreateObservableGauge(
        "simplestore.inventory.projector.lag",
        () => _projectorLagProvider(),
        unit: "bytes",
        description: "Commit-log position delta between KurrentDB's tail and the projector's last applied checkpoint. 0 = caught up.");

    public static void SetProjectorLagProvider(Func<long> provider) => _projectorLagProvider = provider;
}
