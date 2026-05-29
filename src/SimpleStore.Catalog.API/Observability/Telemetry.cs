using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SimpleStore.Catalog.API.Observability;

internal static class Telemetry
{
    public const string SourceName = "SimpleStore.Catalog";

    public static readonly ActivitySource Source = new(SourceName);
    public static readonly Meter Meter = new(SourceName);
}
