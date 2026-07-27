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
            var safePath = SafePath(context.Request.Path);
            logger.LogInformation(
                "HTTP {Method} {Path} completed {StatusCode} in {ElapsedMilliseconds}ms playerId={PlayerId} gameId={GameId}",
                context.Request.Method,
                safePath,
                context.Response.StatusCode,
                elapsed.TotalMilliseconds,
                context.User.FindFirstValue(ClaimTypes.NameIdentifier),
                gameId);
        }
    }

    private static string SafePath(PathString path)
    {
        if (path.StartsWithSegments("/api/replay-shares"))
        {
            return "/api/replay-shares/{redacted}";
        }

        if (path.StartsWithSegments("/shared/replay"))
        {
            return "/shared/replay/{redacted}";
        }

        return path.Value ?? string.Empty;
    }
}

public sealed class CompressedReplayResponseSizeMiddleware(
    RequestDelegate next,
    MistChessMetrics metrics)
    : ReplayResponseSizeMiddleware(next, metrics, compressed: true);

public sealed class UncompressedReplayResponseSizeMiddleware(
    RequestDelegate next,
    MistChessMetrics metrics)
    : ReplayResponseSizeMiddleware(next, metrics, compressed: false);

public abstract class ReplayResponseSizeMiddleware
{
    private readonly RequestDelegate next;
    private readonly MistChessMetrics metrics;
    private readonly bool compressed;

    protected ReplayResponseSizeMiddleware(
        RequestDelegate next,
        MistChessMetrics metrics,
        bool compressed)
    {
        this.next = next;
        this.metrics = metrics;
        this.compressed = compressed;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsReplayPath(context.Request.Path))
        {
            await next(context);
            return;
        }

        var originalBody = context.Response.Body;
        var countingBody = new CountingWriteStream(originalBody);
        context.Response.Body = countingBody;
        try
        {
            await next(context);
        }
        finally
        {
            context.Response.Body = originalBody;
            metrics.RecordReplayResponseSize(
                compressed,
                countingBody.BytesWritten,
                context.Response.StatusCode);
        }
    }

    private static bool IsReplayPath(PathString path)
    {
        return path.StartsWithSegments("/api/replay-shares") ||
               (path.StartsWithSegments("/api/games") &&
                path.Value?.EndsWith("/replay", StringComparison.OrdinalIgnoreCase) == true);
    }

    private sealed class CountingWriteStream(Stream inner) : Stream
    {
        public long BytesWritten { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            BytesWritten += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            BytesWritten += buffer.Length;
        }

        public override async Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            await inner.WriteAsync(buffer, offset, count, cancellationToken);
            BytesWritten += count;
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            BytesWritten += buffer.Length;
        }

        protected override void Dispose(bool disposing)
        {
        }
    }
}
