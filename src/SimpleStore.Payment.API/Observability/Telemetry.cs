using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SimpleStore.Payment.API.Observability;

// v12: per-service ActivitySource + Meter convention. ServiceDefaults' wildcard registration
// (AddSource("SimpleStore.*") / AddMeter("SimpleStore.*")) picks these up automatically.
internal static class Telemetry
{
    public const string SourceName = "SimpleStore.Payment";

    public static readonly ActivitySource Source = new(SourceName);
    public static readonly Meter Meter = new(SourceName);

    public static readonly Counter<long> PaymentsSucceeded = Meter.CreateCounter<long>(
        "simplestore.payments.succeeded",
        description: "Count of order payments charged successfully.");

    public static readonly Counter<long> PaymentsFailed = Meter.CreateCounter<long>(
        "simplestore.payments.failed",
        description: "Count of order payments rejected (tagged by reason, e.g. InsufficientFunds).");

    public static readonly Counter<long> Deposits = Meter.CreateCounter<long>(
        "simplestore.payments.deposits",
        description: "Count of account deposits (top-ups).");
}
