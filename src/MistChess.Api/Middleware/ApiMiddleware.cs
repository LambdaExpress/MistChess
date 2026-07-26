using System.Diagnostics;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using MistChess.Api.Application;
using MistChess.Api.Contracts;
using Npgsql;

namespace MistChess.Api.Middleware;

public sealed class PreAuthenticationRateLimitMiddleware : IMiddleware, IDisposable
{
    private readonly PartitionedRateLimiter<string> limiter =
        PartitionedRateLimiter.Create<string, string>(
            static key => RateLimitPartition.GetFixedWindowLimiter(
                key,
                static _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 120,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {

        using var lease = await limiter.AcquireAsync(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            permitCount: 1,
            context.RequestAborted);
        if (!lease.IsAcquired)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(
                new ErrorResponse("RATE_LIMITED", "Too many requests."),
                context.RequestAborted);
            return;
        }

        await next(context);
    }

    public void Dispose() => limiter.Dispose();
}

public sealed class ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (ApiException exception)
        {
            logger.LogWarning(
                "API request rejected: code={ErrorCode} gameId={GameId} playerId={PlayerId}",
                exception.Code,
                exception.GameId,
                context.User.FindFirstValue(ClaimTypes.NameIdentifier));
            await WriteErrorAsync(context, exception.StatusCode, exception.ToResponse());
        }
        catch (DbUpdateConcurrencyException exception)
        {
            logger.LogWarning(exception, "Database concurrency conflict for playerId={PlayerId}", context.User.FindFirstValue(ClaimTypes.NameIdentifier));
            await WriteErrorAsync(
                context,
                StatusCodes.Status409Conflict,
                new ErrorResponse("STALE_VERSION", "The resource changed while the command was being processed."));
        }
        catch (DbUpdateException exception)
        {
            logger.LogWarning(exception, "Database constraint conflict for playerId={PlayerId}", context.User.FindFirstValue(ClaimTypes.NameIdentifier));
            await WriteErrorAsync(
                context,
                StatusCodes.Status409Conflict,
                new ErrorResponse("CONFLICT", "The command conflicts with the current resource state."));
        }
        catch (PostgresException exception) when (exception.SqlState is "40001" or "40P01")
        {
            logger.LogWarning(exception, "Database transaction conflict for playerId={PlayerId}", context.User.FindFirstValue(ClaimTypes.NameIdentifier));
            await WriteErrorAsync(
                context,
                StatusCodes.Status409Conflict,
                new ErrorResponse("CONFLICT", "The command conflicted with another transaction."));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unhandled API failure for playerId={PlayerId}", context.User.FindFirstValue(ClaimTypes.NameIdentifier));
            await WriteErrorAsync(
                context,
                StatusCodes.Status500InternalServerError,
                new ErrorResponse("INTERNAL_ERROR", "The request could not be completed."));
        }
    }

    private static async Task WriteErrorAsync(HttpContext context, int statusCode, ErrorResponse response)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(response);
    }
}

public sealed class SafeRequestLoggingMiddleware(RequestDelegate next, ILogger<SafeRequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started);
            context.Request.RouteValues.TryGetValue("gameId", out var gameId);
            logger.LogInformation(
                "HTTP {Method} {Path} completed {StatusCode} in {ElapsedMilliseconds}ms playerId={PlayerId} gameId={GameId}",
                context.Request.Method,
                context.Request.Path,
                context.Response.StatusCode,
                elapsed.TotalMilliseconds,
                context.User.FindFirstValue(ClaimTypes.NameIdentifier),
                gameId);
        }
    }
}
