using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SimpleStore.Cart.API.Observability;

internal static class Telemetry
{
    public const string SourceName = "SimpleStore.Cart";

    public static readonly ActivitySource Source = new(SourceName);
    public static readonly Meter Meter = new(SourceName);

    // ProductUpdatedConsumer SCANs the entire cart key space on every product update. The
    // histogram surfaces the duration of that fan-out so it's easy to spot when the cart-key
    // count grows past the "small/medium" comfort zone (see CLAUDE.md's note on revisiting
    // the reverse-index decision).
    public static readonly Histogram<double> CartFanoutDuration = Meter.CreateHistogram<double>(
        "simplestore.cart.fanout.duration",
        unit: "ms",
        description: "Duration of ProductUpdatedConsumer's SCAN-based fan-out across cart keys.");
}
