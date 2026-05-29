using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SimpleStore.Web.Observability;

internal static class Telemetry
{
    public const string SourceName = "SimpleStore.Web";

    public static readonly ActivitySource Source = new(SourceName);
    public static readonly Meter Meter = new(SourceName);

    // Measures the effectiveness of v9 §8's single-flight TokenRefreshCoordinator: increments
    // on every cache HIT (concurrent caller joining an in-flight refresh) rather than every
    // call. A counter of 0 means no coalescing happened; a counter > 0 means the coordinator
    // actually deduplicated.
    public static readonly Counter<long> TokenRefreshCoalesced = Meter.CreateCounter<long>(
        "simplestore.identity.token_refresh.coalesced",
        description: "Count of token refresh calls that joined an in-flight rotation instead of starting a new one.");
}
