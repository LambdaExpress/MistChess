using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using MistChess.Api.Security;
using MistChess.Infrastructure.Persistence;

namespace MistChess.Api.Application;

public sealed class GuestPresenceService(MistChessDbContext db, TimeProvider timeProvider)
{
    public static readonly TimeSpan WriteInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(90);

    public Task<int> TouchAsync(Guid playerId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var writeBefore = now.Subtract(WriteInterval);
        return db.GuestSessions
            .Where(session =>
                session.Id == playerId &&
                session.ExpiresAt > now &&
                session.IsBanned == false &&
                session.LastSeenAt < writeBefore)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(session => session.LastSeenAt, now),
                cancellationToken);
    }

    public static bool IsOnline(GuestSessionEntity session, DateTimeOffset observedAt) =>
        session.ExpiresAt > observedAt &&
        session.IsBanned == false &&
        session.LastSeenAt >= observedAt.Subtract(OnlineWindow);
}

public sealed class GuestPresenceMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        GuestPresenceService presence)
    {
        var endpoint = context.GetEndpoint();
        var playerApi = context.Request.Path.StartsWithSegments("/api") &&
            context.Request.Path.StartsWithSegments("/api/admin") == false &&
            endpoint?.Metadata.GetMetadata<IAuthorizeData>() is not null &&
            endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null;
        if (playerApi &&
            string.Equals(
                context.User.Identity?.AuthenticationType,
                GuestAuthenticationDefaults.Scheme,
                StringComparison.Ordinal) &&
            context.User.HasClaim(MistChessClaims.Banned, bool.TrueString) == false &&
            CurrentPlayer.TryGetId(context.User) is { } playerId)
        {
            await presence.TouchAsync(playerId, context.RequestAborted);
        }

        await next(context);
    }
}
