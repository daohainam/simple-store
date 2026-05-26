using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace SimpleStore.Cart.API.Client;

public static class CartApiClientExtensions
{
    /// <summary>
    /// Registers a typed <see cref="ICartApiClient"/> resolved through Aspire service discovery.
    /// The <paramref name="serviceName"/> must match the name used in AppHost.cs (default: "gateway" — the API gateway).
    /// </summary>
    public static IHttpClientBuilder AddCartApiClient(
        this IHostApplicationBuilder builder,
        string serviceName = "gateway")
    {
        return builder.Services.AddHttpClient<ICartApiClient, CartApiClient>(client =>
        {
            client.BaseAddress = new Uri($"https+http://{serviceName}");
        });
    }
}
