using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace SimpleStore.Catalog.API.Client;

public static class CatalogApiClientExtensions
{
    /// <summary>
    /// Registers a typed <see cref="ICatalogApiClient"/> resolved through Aspire service discovery.
    /// The <paramref name="serviceName"/> must match the name used in AppHost.cs (default: "catalog").
    /// </summary>
    public static IHttpClientBuilder AddCatalogApiClient(
        this IHostApplicationBuilder builder,
        string serviceName = "catalog")
    {
        return builder.Services.AddHttpClient<ICatalogApiClient, CatalogApiClient>(client =>
        {
            // "https+http://" lets Aspire prefer HTTPS and fall back to HTTP.
            client.BaseAddress = new Uri($"https+http://{serviceName}");
        });
    }
}
