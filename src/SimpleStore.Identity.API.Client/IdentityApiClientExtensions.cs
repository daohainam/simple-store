using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace SimpleStore.Identity.API.Client;

public static class IdentityApiClientExtensions
{
    /// <summary>
    /// Registers a typed <see cref="IIdentityApiClient"/> resolved through Aspire service discovery.
    /// The <paramref name="serviceName"/> must match the name used in AppHost.cs (default: "identity").
    /// </summary>
    public static IHttpClientBuilder AddIdentityApiClient(
        this IHostApplicationBuilder builder,
        string serviceName = "identity")
    {
        return builder.Services.AddHttpClient<IIdentityApiClient, IdentityApiClient>(client =>
        {
            // "https+http://" lets Aspire prefer HTTPS and fall back to HTTP.
            client.BaseAddress = new Uri($"https+http://{serviceName}");
        });
    }
}
