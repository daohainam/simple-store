using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace SimpleStore.Payment.API.Client;

public static class PaymentApiClientExtensions
{
    /// <summary>
    /// Registers a typed <see cref="IPaymentApiClient"/> resolved through Aspire service discovery.
    /// The <paramref name="serviceName"/> must match the name used in AppHost.cs (default: "gateway" — the API gateway).
    /// </summary>
    public static IHttpClientBuilder AddPaymentApiClient(
        this IHostApplicationBuilder builder,
        string serviceName = "gateway")
    {
        return builder.Services.AddHttpClient<IPaymentApiClient, PaymentApiClient>(client =>
        {
            client.BaseAddress = new Uri($"https+http://{serviceName}");
        });
    }
}
