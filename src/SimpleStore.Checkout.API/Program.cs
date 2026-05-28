using MassTransit;
using Microsoft.EntityFrameworkCore;
using Quartz;
using SimpleStore.Checkout.API.Data;
using SimpleStore.Checkout.API.Sagas;
using SimpleStore.ServiceDefaults;

// SimpleStore.Checkout.API — v8 checkout saga orchestrator.
//
// Pure consumer/orchestrator: NO HTTP surface, NO JWT (it only reacts to RabbitMQ messages).
// It consumes OrderSubmittedEvent, drives a MassTransit saga state machine, asks Inventory.API to
// reserve stock, and tells Order.API whether to confirm or cancel the order. Saga state lives in
// checkoutdb (Postgres) via the MassTransit EF saga repository.

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// v9: EF Core retry-on-failure for transient Postgres errors. See Identity.API/Program.cs for rationale.
builder.AddNpgsqlDbContext<CheckoutDbContext>("checkoutdb",
    configureSettings: settings =>
    {
        settings.DisableRetry = true;
        settings.CommandTimeout = 30;
    },
    configureDbContextOptions: opt =>
        opt.UseNpgsql(npgsql =>
            npgsql.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(10),
                errorCodesToAdd: null)));

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
        // v9: Rabbit heartbeat + MassTransit retry/CB. See Order.API/Program.cs for rationale.
        // MassTransit applies UseMessageRetry to saga consumers automatically; the saga repository's
        // pessimistic-concurrency row lock (set above) serializes concurrent dispatches per saga.
        cfg.Host(new Uri(builder.Configuration.GetConnectionString("rabbitmq")!), h =>
        {
            h.Heartbeat(TimeSpan.FromSeconds(30));
            h.RequestedConnectionTimeout(TimeSpan.FromSeconds(10));
        });

        cfg.UsePublishMessageScheduler();

        cfg.UseMessageRetry(r => r.Exponential(
            retryLimit: 5,
            minInterval: TimeSpan.FromSeconds(1),
            maxInterval: TimeSpan.FromSeconds(30),
            intervalDelta: TimeSpan.FromSeconds(2)));

        cfg.UseCircuitBreaker(cb =>
        {
            cb.TrackingPeriod = TimeSpan.FromMinutes(1);
            cb.TripThreshold = 15;
            cb.ActiveThreshold = 10;
            cb.ResetInterval = TimeSpan.FromMinutes(5);
        });

        cfg.ConfigureEndpoints(ctx);
    });
});

var app = builder.Build();

app.MapDefaultEndpoints();

// Migrate on startup. Checkout owns checkoutdb's schema (saga state + MassTransit outbox tables).
// v9: wrapped in StartupMigrationRunner — see Identity.API/Program.cs.
await StartupMigrationRunner.RunAsync(app, async (sp, ct) =>
{
    var db = sp.GetRequiredService<CheckoutDbContext>();
    await db.Database.MigrateAsync(ct);
});

app.Run();
