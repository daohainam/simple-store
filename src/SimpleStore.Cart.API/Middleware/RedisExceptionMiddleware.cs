using System.Text.Json;
using StackExchange.Redis;

namespace SimpleStore.Cart.API.Middleware;

// v9: turns Redis transient failures bubbling out of cart endpoints into a clean 503 Service
// Unavailable instead of the framework's default unhandled-exception 500. The storefront treats
// 503 as "try again shortly" without exposing stack traces to the client.
//
// Read-only paths (GET /api/cart, /count, /total) degrade to an empty cart inside RedisCartStore;
// only the read-modify-write paths reach this middleware on a Redis outage.
public sealed class RedisExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RedisExceptionMiddleware> _log;

    public RedisExceptionMiddleware(RequestDelegate next, ILogger<RedisExceptionMiddleware> log)
    {
        _next = next;
        _log = log;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await _next(ctx);
        }
        catch (RedisConnectionException ex)
        {
            await WriteUnavailable(ctx, ex, "Redis unreachable.");
        }
        catch (RedisTimeoutException ex)
        {
            await WriteUnavailable(ctx, ex, "Redis timed out.");
        }
    }

    private async Task WriteUnavailable(HttpContext ctx, Exception ex, string reason)
    {
        _log.LogWarning(ex,
            "Cart request {Method} {Path} failed: {Reason}", ctx.Request.Method, ctx.Request.Path, reason);

        if (ctx.Response.HasStarted)
        {
            // Response already partially written — nothing safe to do beyond letting the connection drop.
            return;
        }

        ctx.Response.Clear();
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        ctx.Response.Headers.RetryAfter = "5";
        ctx.Response.ContentType = "application/problem+json";

        var problem = new
        {
            type = "https://httpstatuses.io/503",
            title = "Cart temporarily unavailable",
            status = StatusCodes.Status503ServiceUnavailable,
            detail = "The cart store is briefly unreachable. Please retry shortly."
        };
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(problem));
    }
}
