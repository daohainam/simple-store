using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SimpleStore.Identity.API.Observability;

internal static class Telemetry
{
    public const string SourceName = "SimpleStore.Identity";

    public static readonly ActivitySource Source = new(SourceName);
    public static readonly Meter Meter = new(SourceName);
}
