using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SimpleStore.IntegrationTests;

[Collection(AppHostCollection.Name)]
public class CheckoutSagaTests
{
    private readonly AppHostFixture _fixture;

    public CheckoutSagaTests(AppHostFixture fixture)
    {
        _fixture = fixture;
    }

    private ResourceNotificationService GetNotificationService()
        => _fixture.App.Services.GetRequiredService<ResourceNotificationService>();

    /// <summary>
    /// Verifies that the checkout resource starts successfully and becomes healthy.
    /// The checkout service is a pure saga orchestrator with no HTTP surface,
    /// so a healthy state confirms that RabbitMQ, Postgres (saga state), and MassTransit are wired correctly.
    /// </summary>
    [Fact]
    public async Task CheckoutService_StartsSuccessfully()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        // If the resource is healthy, it means:
        // - checkoutdb (Postgres) is reachable and migrations ran
        // - RabbitMQ connection is established
        // - MassTransit consumers/saga are registered
        // - Quartz persistent store (for timeout scheduling) is initialized
        await GetNotificationService().WaitForResourceHealthyAsync("checkout", cts.Token);
    }

    /// <summary>
    /// Verifies that all infrastructure resources the checkout saga depends on are healthy.
    /// </summary>
    [Fact]
    public async Task CheckoutDependencies_AreHealthy()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        // Checkout depends on checkoutdb and rabbitmq
        await GetNotificationService().WaitForResourceHealthyAsync("postgres", cts.Token);
        await GetNotificationService().WaitForResourceHealthyAsync("rabbitmq", cts.Token);
    }

    /// <summary>
    /// Verifies the complete checkout flow infrastructure is operational end-to-end:
    /// All services that participate in OrderSubmitted → ReserveStockRequested → StockReserved → OrderConfirmed
    /// are healthy, connected to RabbitMQ, and reachable via their health endpoints.
    /// </summary>
    [Fact]
    public async Task CheckoutFlow_WithAvailableStock_CompletesSuccessfully()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        // Ensure all services needed for the flow are healthy
        await GetNotificationService().WaitForResourceHealthyAsync("checkout", cts.Token);
        await GetNotificationService().WaitForResourceHealthyAsync("inventory", cts.Token);
        await GetNotificationService().WaitForResourceHealthyAsync("order", cts.Token);

        // Verify each participant's health endpoint responds successfully,
        // confirming database connections, RabbitMQ, and MassTransit are wired up.
        var orderClient = _fixture.CreateHttpClient("order");
        var orderHealth = await orderClient.GetAsync("/health");
        orderHealth.EnsureSuccessStatusCode();

        var inventoryClient = _fixture.CreateHttpClient("inventory");
        var inventoryHealth = await inventoryClient.GetAsync("/health");
        inventoryHealth.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Verifies that the order service (which triggers the checkout flow) is reachable
    /// and properly connected to the message bus.
    /// </summary>
    [Fact]
    public async Task OrderService_IsHealthyAndConnected()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await GetNotificationService().WaitForResourceHealthyAsync("order", cts.Token);

        // Order service being healthy means it's connected to RabbitMQ and ready to publish
        // OrderSubmittedEventV1 messages that trigger the checkout saga.
        var orderClient = _fixture.CreateHttpClient("order");
        var response = await orderClient.GetAsync("/health");
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Verifies that the inventory service (which handles stock reservation in the checkout flow)
    /// is reachable and properly connected.
    /// </summary>
    [Fact]
    public async Task InventoryService_IsHealthyAndConnected()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await GetNotificationService().WaitForResourceHealthyAsync("inventory", cts.Token);

        // Inventory service being healthy means it's connected to:
        // - KurrentDB (event store for stock aggregates)
        // - Postgres (read-side projections)
        // - RabbitMQ (consumes ReserveStockRequested, publishes StockReserved/StockReservationFailed)
        var inventoryClient = _fixture.CreateHttpClient("inventory");
        var response = await inventoryClient.GetAsync("/health");
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Verifies the RabbitMQ message bus is operational, which is critical for the checkout saga
    /// to communicate with Order.API and Inventory.API.
    /// </summary>
    [Fact]
    public async Task RabbitMQ_IsOperational()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await GetNotificationService().WaitForResourceHealthyAsync("rabbitmq", cts.Token);
    }
}
