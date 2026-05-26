using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace SimpleStore.Inventory.API.Client;

public static class InventoryApiClientExtensions
{
    /// <summary>
    /// Registers a typed <see cref="IInventoryApiClient"/> resolved through Aspire service discovery.
    /// The <paramref name="serviceName"/> must match the name used in AppHost.cs (default: "gateway" — the API gateway).
    /// </summary>
    public static IHttpClientBuilder AddInventoryApiClient(
        this IHostApplicationBuilder builder,
        string serviceName = "gateway")
    {
        return builder.Services.AddHttpClient<IInventoryApiClient, InventoryApiClient>(client =>
        {
            client.BaseAddress = new Uri($"https+http://{serviceName}");
        });
    }
}
