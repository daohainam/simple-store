using KurrentDB.Client;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SimpleStore.Inventory.API.Infrastructure;

// v9: readiness probe for KurrentDB. Aspire's KurrentDB resource doesn't ship a health-check
// auto-registration, so we add one — when KurrentDB is down the Inventory projector loses its
// event source and the read model goes stale. Reading a single event from $all is the cheapest
// liveness signal the SDK exposes; on a brand-new event store the stream may be empty (StreamNotFound),
// which we treat as Healthy because the connection itself succeeded.
public sealed class KurrentDbHealthCheck : IHealthCheck
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    private readonly KurrentDBClient _client;

    public KurrentDbHealthCheck(KurrentDBClient client)
    {
        _client = client;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ProbeTimeout);

        try
        {
            // ReadAllAsync from the end with maxCount=1 is the cheapest connectivity probe the
            // SDK exposes. Empty event store → the enumeration completes without yielding; either
            // outcome confirms the gRPC channel is up.
            var result = _client.ReadAllAsync(
                direction: Direction.Backwards,
                position: Position.End,
                maxCount: 1,
                cancellationToken: cts.Token);

            await foreach (var _ in result.WithCancellation(cts.Token))
            {
                break;
            }
            return HealthCheckResult.Healthy("KurrentDB reachable.");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("KurrentDB probe timed out.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("KurrentDB probe failed.", ex);
        }
    }
}
