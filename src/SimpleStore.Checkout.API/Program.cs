using MassTransit;
using Microsoft.EntityFrameworkCore;
using Quartz;
using SimpleStore.Checkout.API.Data;
using SimpleStore.Checkout.API.Sagas;

// SimpleStore.Checkout.API — v8 checkout saga orchestrator.
//
// Pure consumer/orchestrator: NO HTTP surface, NO JWT (it only reacts to RabbitMQ messages).
// It consumes OrderSubmittedEvent, drives a MassTransit saga state machine, asks Inventory.API to
// reserve stock, and tells Order.API whether to confirm or cancel the order. Saga state lives in
// checkoutdb (Postgres) via the MassTransit EF saga repository.

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.AddNpgsqlDbContext<CheckoutDbContext>("checkoutdb");

// In-memory Quartz scheduler backs the saga's reservation timeout. No RabbitMQ delayed-exchange
// plugin required (the standard broker image doesn't ship one). Trade-off: scheduled timeouts do
// NOT survive a Checkout.API restart — acceptable for the sample (see docs/checkout-saga.md §11.2).
builder.Services.AddQuartz();
builder.Services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);

builder.Services.AddMassTransit(x =>
{
    // Bus outbox: messages the saga publishes commit atomically with the saga-state change.
    x.AddEntityFrameworkOutbox<CheckoutDbContext>(o =>
    {
        o.UsePostgres();
        o.UseBusOutbox();
    });

    x.AddSagaStateMachine<CheckoutSagaStateMachine, CheckoutSagaState>()
        .EntityFrameworkRepository(r =>
        {
            r.ConcurrencyMode = ConcurrencyMode.Pessimistic; // row-lock the saga instance per message
            r.ExistingDbContext<CheckoutDbContext>();
            r.UsePostgres();
        });

    // Quartz-backed message scheduler (in-memory) used by the saga's Schedule(...) timeout.
    x.AddQuartzConsumers();
    x.AddPublishMessageScheduler();

    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(new Uri(builder.Configuration.GetConnectionString("rabbitmq")!));
        cfg.UsePublishMessageScheduler();
        cfg.ConfigureEndpoints(ctx);
    });
});

var app = builder.Build();

app.MapDefaultEndpoints();

// Migrate on startup. Checkout owns checkoutdb's schema (saga state + MassTransit outbox tables).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CheckoutDbContext>();
    await db.Database.MigrateAsync();
}

app.Run();
