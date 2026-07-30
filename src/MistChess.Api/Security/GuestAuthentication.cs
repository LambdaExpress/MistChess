using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MistChess.Api.Application;
using MistChess.Api.Contracts;
using MistChess.Infrastructure.Persistence;

namespace MistChess.Api.Security;

public static class GuestAuthenticationDefaults
{
    public const string Scheme = "MistChessGuest";
    public const string CookieName = "__Host-MistChessGuest";
    public const string DevelopmentCookieName = "MistChessGuest";

    public static string GetCookieName(IHostEnvironment environment) =>
        environment.IsDevelopment() ? DevelopmentCookieName : CookieName;
}

public sealed class GuestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IDbContextFactory<MistChessDbContext> contextFactory,
    TimeProvider timeProvider,
    IHostEnvironment environment)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(GuestAuthenticationDefaults.GetCookieName(environment), out var token) ||
            string.IsNullOrWhiteSpace(token))
        {
            return AuthenticateResult.NoResult();
        }

        var tokenHash = GuestToken.Hash(token);
        var now = timeProvider.GetUtcNow();
        await using var db = await contextFactory.CreateDbContextAsync(Context.RequestAborted);
        var session = await db.GuestSessions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                value => value.TokenHash == tokenHash && value.ExpiresAt > now,
                Context.RequestAborted);
        if (session is null)
        {
            return AuthenticateResult.Fail("The guest session is invalid or expired.");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, session.Id.ToString("D")),
            new(ClaimTypes.Name, session.DisplayName),
            new(MistChessClaims.PrincipalKind, MistChessClaims.GuestPrincipal)
        };
        if (session.IsBanned)
        {
            claims.Add(new Claim(MistChessClaims.Banned, bool.TrueString));
            claims.Add(new Claim(MistChessClaims.BanReason, session.BanReason ?? string.Empty));
        }
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, GuestAuthenticationDefaults.Scheme));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, GuestAuthenticationDefaults.Scheme));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        WriteAuthenticationErrorAsync(
            StatusCodes.Status401Unauthorized,
            new ErrorResponse("UNAUTHORIZED", "A valid guest session is required."));

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        if (Context.User.HasClaim(MistChessClaims.Banned, bool.TrueString))
        {
            return WriteAuthenticationErrorAsync(
                StatusCodes.Status403Forbidden,
                new ErrorResponse(
                    "PLAYER_BANNED",
                    "This player is banned.",
                    Context.User.FindFirstValue(MistChessClaims.BanReason)));
        }

        return WriteAuthenticationErrorAsync(
            StatusCodes.Status403Forbidden,
            new ErrorResponse("FORBIDDEN", "This guest session cannot access the requested resource."));
    }

    private Task WriteAuthenticationErrorAsync(int statusCode, ErrorResponse error)
    {
        Response.StatusCode = statusCode;
        Response.ContentType = "application/json";
        return Response.WriteAsJsonAsync(error, Context.RequestAborted);
    }
}

public sealed class GuestSessionService(
    MistChessDbContext db,
    TimeProvider timeProvider,
    IHostEnvironment environment)
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);

    public async Task<GuestSessionView> RestoreOrCreateAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var existingPlayerId = CurrentPlayer.TryGetId(context.User);
        if (existingPlayerId is { } playerId)
        {
            var existing = await db.GuestSessions
                .AsNoTracking()
                .Where(value => value.Id == playerId)
                .Select(value => new
                {
                    value.Id,
                    value.DisplayName,
                    ActiveGameId = db.GamePlayers
                        .Where(participant =>
                            participant.PlayerId == value.Id &&
                            participant.IsActive)
                        .Select(participant => (Guid?)participant.GameId)
                        .SingleOrDefault(),
                    value.IsBanned,
                    value.BanReason,
                })
                .SingleAsync(cancellationToken);
            if (existing.IsBanned)
            {
                throw new ApiException(
                    StatusCodes.Status403Forbidden,
                    "PLAYER_BANNED",
                    "This player is banned.",
                    existing.BanReason);
            }

            return new GuestSessionView(
                existing.Id,
                existing.DisplayName,
                existing.ActiveGameId);
        }

        var now = timeProvider.GetUtcNow();
        var token = GuestToken.Create();
        var session = new GuestSessionEntity
        {
            Id = Guid.NewGuid(),
            TokenHash = GuestToken.Hash(token),
            DisplayName = $"Guest {RandomNumberGenerator.GetInt32(100000, 1000000)}",
            CreatedAt = now,
            ExpiresAt = now.Add(SessionLifetime),
            LastSeenAt = now
        };
        db.GuestSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);

        context.Response.Cookies.Append(
            GuestAuthenticationDefaults.GetCookieName(environment),
            token,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = !environment.IsDevelopment(),
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                Path = "/",
                Expires = session.ExpiresAt
            });

        return new GuestSessionView(
            session.Id,
            session.DisplayName,
            null);
    }
}

internal static class GuestSessionBootstrapRequest
{
    public static bool IsTrusted(HttpRequest request) =>
        HeaderEquals(request, "X-Requested-With", "MistChess", StringComparison.Ordinal) ||
        HeaderEquals(request, "Sec-Fetch-Site", "same-origin", StringComparison.OrdinalIgnoreCase);

    private static bool HeaderEquals(
        HttpRequest request,
        string headerName,
        string expectedValue,
        StringComparison comparison)
    {
        return request.Headers.TryGetValue(headerName, out var values) &&
            values.Count == 1 &&
            string.Equals(values[0], expectedValue, comparison);
    }
}

public static class CurrentPlayer
{
    public static Guid GetId(ClaimsPrincipal principal) =>
        TryGetId(principal) ?? throw ApiException.Unauthorized();

    public static Guid? TryGetId(ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var playerId) ? playerId : null;
    }
}

internal static class GuestToken
{
    public static string Create() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
