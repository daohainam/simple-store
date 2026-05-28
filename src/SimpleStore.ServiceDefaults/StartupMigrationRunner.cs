using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SimpleStore.ServiceDefaults;

// v9: bounded-retry wrapper around the startup migration + seeding block that every service runs
// before app.Run(). Without this, a transient Postgres unreachability at boot (rolling Postgres
// restart, slow container start) throws an unhandled exception and the service crash-loops.
// Five attempts with exponential backoff (1 s → 16 s) gives Postgres plenty of time to come up
// while still failing fast if the connection string is genuinely wrong.
public static class StartupMigrationRunner
{
    private const int DefaultMaxAttempts = 5;
    private static readonly TimeSpan MinDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(16);

    public static async Task RunAsync(
        IHost app,
        Func<IServiceProvider, CancellationToken, Task> migrate,
        int maxAttempts = DefaultMaxAttempts,
        CancellationToken cancellationToken = default)
    {
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("StartupMigration");
        var delay = MinDelay;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var scope = app.Services.CreateScope();
                await migrate(scope.ServiceProvider, cancellationToken);
                if (attempt > 1)
                {
                    log.LogInformation(
                        "Startup migration succeeded on attempt {Attempt}.", attempt);
                }
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
            {
                log.LogWarning(ex,
                    "Startup migration attempt {Attempt}/{MaxAttempts} failed; retrying in {DelaySeconds}s.",
                    attempt, maxAttempts, delay.TotalSeconds);
                try
                {
                    await Task.Delay(delay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                delay = TimeSpan.FromSeconds(Math.Min(MaxDelay.TotalSeconds, delay.TotalSeconds * 2));
            }
        }
    }
}
