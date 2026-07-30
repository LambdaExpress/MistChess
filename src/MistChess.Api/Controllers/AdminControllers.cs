using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MistChess.Api.Application;
using MistChess.Api.Contracts;
using MistChess.Api.Security;

namespace MistChess.Api.Controllers;

[ApiController]
[Authorize(Policy = AdminAuthenticationDefaults.AuthorizationPolicy)]
[Route("api/admin")]
public sealed class AdminSessionController(
    IAntiforgery antiforgery,
    AdminCredentialService credentials,
    TimeProvider timeProvider,
    ILogger<AdminSessionController> logger) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("antiforgery/token", Name = "getAdminAntiforgeryToken")]
    [ProducesResponseType<AntiforgeryTokenView>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AntiforgeryTokenView>> AntiforgeryToken()
    {
        Response.Headers.CacheControl = "no-store";
        var adminAuthentication = await HttpContext.AuthenticateAsync(
            AdminAuthenticationDefaults.Scheme);
        if (adminAuthentication.Succeeded && adminAuthentication.Principal is not null)
        {
            HttpContext.User = adminAuthentication.Principal;
        }

        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new AntiforgeryTokenView(
            tokens.RequestToken ?? throw new InvalidOperationException("An antiforgery request token was not created."),
            tokens.HeaderName ?? throw new InvalidOperationException("An antiforgery header name was not configured.")));
    }

    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    [HttpPost("session", Name = "createAdminSession")]
    [ProducesResponseType<AdminSessionView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AdminSessionView>> Login(
        [FromBody] AdminLoginRequest request)
    {
        Response.Headers.CacheControl = "no-store";
        var source = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var session = credentials.Verify(request, source);
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, session.Username),
            new Claim(MistChessClaims.PrincipalKind, MistChessClaims.AdminPrincipal)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            AdminAuthenticationDefaults.Scheme));
        await HttpContext.SignInAsync(
            AdminAuthenticationDefaults.Scheme,
            principal,
            new AuthenticationProperties
            {
                AllowRefresh = false,
                ExpiresUtc = session.ExpiresAt,
                IsPersistent = false
            });
        return Ok(session);
    }

    [HttpGet("session", Name = "getAdminSession")]
    [ProducesResponseType<AdminSessionView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AdminSessionView>> GetSession()
    {
        Response.Headers.CacheControl = "no-store";
        var authentication = await HttpContext.AuthenticateAsync(AdminAuthenticationDefaults.Scheme);
        var expiresAt = authentication.Properties?.ExpiresUtc
            ?? throw new InvalidOperationException("The administrator authentication ticket has no expiration.");
        return Ok(new AdminSessionView(CurrentAdmin.GetName(User), expiresAt));
    }

    [ValidateAntiForgeryToken]
    [HttpDelete("session", Name = "deleteAdminSession")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout()
    {
        Response.Headers.CacheControl = "no-store";
        var adminName = CurrentAdmin.GetName(User);
        await HttpContext.SignOutAsync(AdminAuthenticationDefaults.Scheme);
        logger.LogInformation(
            "Admin logout succeeded adminName={AdminName} sourceIp={SourceIp} actionTime={ActionTime}",
            adminName,
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            timeProvider.GetUtcNow());
        return NoContent();
    }
}

[ApiController]
[Authorize(Policy = AdminAuthenticationDefaults.AuthorizationPolicy)]
[Route("api/admin/users")]
public sealed class AdminUsersController(AdminUserService users) : ControllerBase
{
    [HttpGet(Name = "getAdminUsers")]
    [EnableRateLimiting("admin-users")]
    [ProducesResponseType<AdminUsersPageView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<AdminUsersPageView>> List(
        [FromQuery] string? query,
        [FromQuery] string? status,
        [FromQuery] string? online,
        [FromQuery] string? cursor,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "private, no-store";
        return Ok(await users.ListAsync(
            query,
            status,
            online,
            cursor,
            limit,
            cancellationToken));
    }

    [HttpGet("{playerId:guid}", Name = "getAdminUser")]
    [EnableRateLimiting("admin-users")]
    [ProducesResponseType<AdminUserDetailView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminUserDetailView>> Detail(
        Guid playerId,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "private, no-store";
        return Ok(await users.DetailAsync(playerId, cancellationToken));
    }

    [HttpPost("{playerId:guid}/ban", Name = "banAdminUser")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("admin-users")]
    [ProducesResponseType<AdminBanStatusView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<AdminBanStatusView>> Ban(
        Guid playerId,
        [FromBody] AdminBanRequest request,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "private, no-store";
        return Ok(await users.BanAsync(
            playerId,
            request.Reason,
            CurrentAdmin.GetName(User),
            cancellationToken));
    }

    [HttpDelete("{playerId:guid}/ban", Name = "unbanAdminUser")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("admin-users")]
    [ProducesResponseType<AdminBanStatusView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminBanStatusView>> Unban(
        Guid playerId,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "private, no-store";
        return Ok(await users.UnbanAsync(
            playerId,
            CurrentAdmin.GetName(User),
            cancellationToken));
    }

    [HttpGet("{playerId:guid}/games", Name = "getAdminUserGames")]
    [EnableRateLimiting("admin-history")]
    [ProducesResponseType<AdminHistoricalGamesPageView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<AdminHistoricalGamesPageView>> History(
        Guid playerId,
        [FromQuery] string? cursor,
        [FromQuery] int limit = 20,
        [FromQuery] string? ruleVersion = null,
        [FromQuery] string? timeControl = null,
        [FromQuery] string? result = null,
        CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "private, no-store";
        return Ok(await users.HistoryAsync(
            playerId,
            cursor,
            limit,
            ruleVersion,
            timeControl,
            result,
            cancellationToken));
    }
}

[ApiController]
[Authorize(Policy = AdminAuthenticationDefaults.AuthorizationPolicy)]
[Route("api/admin/games")]
public sealed class AdminGamesController(HistoryService history) : ControllerBase
{
    [HttpGet("{gameId:guid}/replay", Name = "getAdminGameReplay")]
    [EnableRateLimiting("admin-history")]
    [ProducesResponseType<HistoricalReplayView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<HistoricalReplayView>> Replay(
        Guid gameId,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "private, no-store";
        return Ok(await history.AdminReplayAsync(gameId, cancellationToken));
    }
}
