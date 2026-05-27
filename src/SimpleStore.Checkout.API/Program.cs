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

// Quartz backs the saga's reservation timeout. It uses a PERSISTENT ADO store in checkoutdb (the
// qrtz_* tables, created by the AddQuartzTables migration) rather than the in-memory RAMJobStore,
// so a scheduled timeout SURVIVES a Checkout.API restart: on boot Quartz reloads pending triggers
// and immediately fires any that misfired while the process was down, cancelling stuck orders.
// No RabbitMQ delayed-exchange plugin required. See docs/checkout-saga.md §11.2.
var checkoutDbConnectionString = builder.Configuration.GetConnectionString("checkoutdb")
    ?? throw new InvalidOperationException("Connection string 'checkoutdb' is required for the Quartz persistent store.");

builder.Services.AddQuartz(q =>
{
    q.UsePersistentStore(s =>
    {
        // MassTransit's scheduling job stores its message payload as string properties in the
        // JobDataMap, so UseProperties=true is both compatible and avoids object serialization there.
        s.UseProperties = true;
        // Lowercase table prefix matches the qrtz_* tables created by the migration and is robust
        // regardless of whether the ADO delegate quotes identifiers (Postgres folds unquoted to lower).
        s.UsePostgres(pg =>
        {
            pg.ConnectionString = checkoutDbConnectionString;
            pg.TablePrefix = "qrtz_";
        });
        s.UseSystemTextJsonSerializer();
    });
});
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

    // Quartz-backed message scheduler (persistent ADO store, see above) used by the saga's
    // Schedule(...) timeout.
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
