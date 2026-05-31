using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SimpleStore.IntegrationTests;

/// <summary>
/// Shared Aspire DistributedApplication fixture for integration tests.
/// Starts the full AppHost once and shares it across all tests in the collection.
/// </summary>
public sealed class AppHostFixture : IAsyncLifetime
{
    private DistributedApplication? _app;

    public DistributedApplication App => _app ?? throw new InvalidOperationException("AppHost not started.");

    public async Task InitializeAsync()
    {
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.SimpleStore_AppHost>();

        _app = await appHost.BuildAsync();
        var resourceNotificationService = _app.Services.GetRequiredService<ResourceNotificationService>();
        await _app.StartAsync();

        // Wait for the catalog resource to be running and ready
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await resourceNotificationService.WaitForResourceHealthyAsync("catalog", cts.Token);
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    /// <summary>
    /// Creates an HttpClient configured to talk to the named Aspire resource.
    /// </summary>
    public HttpClient CreateHttpClient(string resourceName)
    {
        return App.CreateHttpClient(resourceName);
    }
}
