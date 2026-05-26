using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace SimpleStore.Order.API.Client;

public static class OrderApiClientExtensions
{
    /// <summary>
    /// Registers a typed <see cref="IOrderApiClient"/> resolved through Aspire service discovery.
    /// The <paramref name="serviceName"/> must match the name used in AppHost.cs (default: "gateway" — the API gateway).
    /// </summary>
    public static IHttpClientBuilder AddOrderApiClient(
        this IHostApplicationBuilder builder,
        string serviceName = "gateway")
    {
        return builder.Services.AddHttpClient<IOrderApiClient, OrderApiClient>(client =>
        {
            client.BaseAddress = new Uri($"https+http://{serviceName}");
        });
    }
}
