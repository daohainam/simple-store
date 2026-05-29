using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SimpleStore.Admin.Observability;

internal static class Telemetry
{
    public const string SourceName = "SimpleStore.Admin";

    public static readonly ActivitySource Source = new(SourceName);
    public static readonly Meter Meter = new(SourceName);

    // Same coalescing counter as Web — see SimpleStore.Web.Observability.Telemetry for rationale.
    public static readonly Counter<long> TokenRefreshCoalesced = Meter.CreateCounter<long>(
        "simplestore.identity.token_refresh.coalesced",
        description: "Count of token refresh calls that joined an in-flight rotation instead of starting a new one.");
}
