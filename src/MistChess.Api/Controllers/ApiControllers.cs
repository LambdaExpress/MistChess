using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MistChess.Api.Application;
using MistChess.Api.Contracts;
using MistChess.Api.Security;
using ApiGameView = MistChess.Api.Contracts.GameView;

namespace MistChess.Api.Controllers;

[ApiController]
[Route("api/game-options")]
public sealed class GameOptionsController : ControllerBase
{
    [AllowAnonymous]
    [HttpGet(Name = "getGameOptions")]
    [ProducesResponseType<GameOptionsView>(StatusCodes.Status200OK)]
    public ActionResult<GameOptionsView> Get() => Ok(GameOptionsCatalog.View);
}

[ApiController]
[Route("api/sessions")]
public sealed class SessionsController(GuestSessionService sessions) : ControllerBase
{
    [AllowAnonymous]
    [EnableRateLimiting("session")]
    [HttpPost("guest", Name = "createGuestSession")]
    [ProducesResponseType<GuestSessionView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<GuestSessionView>> Guest(CancellationToken cancellationToken)
    {
        if (!GuestSessionBootstrapRequest.IsTrusted(Request))
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                new ErrorResponse("CROSS_SITE_REQUEST", "The guest session bootstrap must originate from MistChess."));
        }

        return Ok(await sessions.RestoreOrCreateAsync(HttpContext, cancellationToken));
    }
}

[ApiController]
[Authorize]
[Route("api/antiforgery")]
public sealed class AntiforgeryController(IAntiforgery antiforgery) : ControllerBase
{
    [HttpGet("token", Name = "getAntiforgeryToken")]
    [ProducesResponseType<AntiforgeryTokenView>(StatusCodes.Status200OK)]
    public ActionResult<AntiforgeryTokenView> Token()
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new AntiforgeryTokenView(
            tokens.RequestToken ?? throw new InvalidOperationException("An antiforgery request token was not created."),
            tokens.HeaderName ?? throw new InvalidOperationException("An antiforgery header name was not configured.")));
    }
}

[ApiController]
[Authorize]
[Route("api/rooms")]
public sealed class RoomsController(RoomService rooms) : ControllerBase
{
    [HttpPost(Name = "createRoom")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("resource")]
    [ProducesResponseType<RoomView>(StatusCodes.Status200OK)]
    public async Task<ActionResult<RoomView>> Create(
        [FromBody] CreateRoomRequest request,
        CancellationToken cancellationToken) =>
        Ok(await rooms.CreateAsync(CurrentPlayer.GetId(User), request, cancellationToken));

    [HttpPost("{code}/join", Name = "joinRoom")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("resource")]
    [ProducesResponseType<RoomView>(StatusCodes.Status200OK)]
    public async Task<ActionResult<RoomView>> Join(string code, CancellationToken cancellationToken) =>
        Ok(await rooms.JoinAsync(code, CurrentPlayer.GetId(User), cancellationToken));

    [HttpPost("{code}/ready", Name = "setRoomReady")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("command")]
    [ProducesResponseType<RoomView>(StatusCodes.Status200OK)]
    public async Task<ActionResult<RoomView>> Ready(
        string code,
        [FromBody] SetReadyRequest request,
        CancellationToken cancellationToken) =>
        Ok(await rooms.SetReadyAsync(code, CurrentPlayer.GetId(User), request.Ready, cancellationToken));

    [HttpDelete("{code}/members/me", Name = "leaveRoom")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("command")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Leave(string code, CancellationToken cancellationToken)
    {
        await rooms.LeaveAsync(code, CurrentPlayer.GetId(User), cancellationToken);
        return NoContent();
    }
}

[ApiController]
[Authorize]
[Route("api/matchmaking/tickets")]
public sealed class MatchmakingController(MatchmakingService matchmaking) : ControllerBase
{
    [HttpPost(Name = "createMatchTicket")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("matchmaking")]
    [ProducesResponseType<MatchTicketView>(StatusCodes.Status200OK)]
    public async Task<ActionResult<MatchTicketView>> Create(
        [FromBody] CreateMatchTicketRequest request,
        CancellationToken cancellationToken) =>
        Ok(await matchmaking.CreateAsync(CurrentPlayer.GetId(User), request, cancellationToken));

    [HttpGet("current", Name = "getCurrentMatchTicket")]
    [ProducesResponseType<MatchTicketView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MatchTicketView>> Current(CancellationToken cancellationToken) =>
        Ok(await matchmaking.CurrentAsync(CurrentPlayer.GetId(User), cancellationToken));

    [HttpPost("{ticketId:guid}/heartbeat", Name = "heartbeatMatchTicket")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("matchmaking")]
    [ProducesResponseType<MatchTicketView>(StatusCodes.Status200OK)]
    public async Task<ActionResult<MatchTicketView>> Heartbeat(
        Guid ticketId,
        CancellationToken cancellationToken) =>
        Ok(await matchmaking.HeartbeatAsync(CurrentPlayer.GetId(User), ticketId, cancellationToken));

    [HttpDelete("{ticketId:guid}", Name = "cancelMatchTicket")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("command")]
    [ProducesResponseType<MatchTicketView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MatchTicketView>> Cancel(
        Guid ticketId,
        CancellationToken cancellationToken) =>
        Ok(await matchmaking.CancelAsync(CurrentPlayer.GetId(User), ticketId, cancellationToken));
}

[ApiController]
[Authorize]
[Route("api/games")]
public sealed class GamesController(
    GameService games,
    HistoryService history,
    MistChessMetrics metrics) : ControllerBase
{
    [HttpGet("history", Name = "getGameHistory")]
    [EnableRateLimiting("history-read")]
    [ProducesResponseType<HistoricalGamesPageView>(StatusCodes.Status200OK)]
    public async Task<ActionResult<HistoricalGamesPageView>> History(
        [FromQuery] string? cursor,
        [FromQuery] int limit = 20,
        [FromQuery] string? ruleVersion = null,
        [FromQuery] string? timeControl = null,
        [FromQuery] string? result = null,
        CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "private, no-store";
        return Ok(await history.ListAsync(
            CurrentPlayer.GetId(User),
            cursor,
            limit,
            ruleVersion,
            timeControl,
            result,
            cancellationToken));
    }

    [HttpGet("{gameId:guid}", Name = "getGame")]
    [ProducesResponseType<ApiGameView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiGameView>> Get(Guid gameId, CancellationToken cancellationToken) =>
        Ok(await games.GetAsync(gameId, CurrentPlayer.GetId(User), cancellationToken));

    [HttpPost("{gameId:guid}/moves", Name = "submitMove")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("move")]
    [ProducesResponseType<ApiGameView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApiGameView>> Move(
        Guid gameId,
        [FromBody] MoveRequest request,
        CancellationToken cancellationToken) =>
        Ok(await games.MoveAsync(gameId, CurrentPlayer.GetId(User), request, cancellationToken));

    [HttpPost("{gameId:guid}/resign", Name = "resignGame")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("command")]
    [ProducesResponseType<ApiGameView>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiGameView>> Resign(Guid gameId, CancellationToken cancellationToken) =>
        Ok(await games.ResignAsync(gameId, CurrentPlayer.GetId(User), cancellationToken));

    [HttpPost("{gameId:guid}/draw-offers", Name = "offerDraw")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("command")]
    [ProducesResponseType<DrawOfferView>(StatusCodes.Status200OK)]
    public async Task<ActionResult<DrawOfferView>> OfferDraw(Guid gameId, CancellationToken cancellationToken) =>
        Ok(await games.OfferDrawAsync(gameId, CurrentPlayer.GetId(User), cancellationToken));

    [HttpPost("{gameId:guid}/draw-offers/accept", Name = "acceptDraw")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("command")]
    [ProducesResponseType<ApiGameView>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiGameView>> AcceptDraw(Guid gameId, CancellationToken cancellationToken) =>
        Ok(await games.AcceptDrawAsync(gameId, CurrentPlayer.GetId(User), cancellationToken));

    [HttpPost("{gameId:guid}/draw-offers/reject", Name = "rejectDraw")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("command")]
    [ProducesResponseType<DrawOfferView>(StatusCodes.Status200OK)]
    public async Task<ActionResult<DrawOfferView>> RejectDraw(Guid gameId, CancellationToken cancellationToken) =>
        Ok(await games.RejectDrawAsync(gameId, CurrentPlayer.GetId(User), cancellationToken));

    [HttpGet("{gameId:guid}/replay", Name = "getReplay")]
    [EnableRateLimiting("history-read")]
    [ProducesResponseType<HistoricalReplayView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<HistoricalReplayView>> Replay(
        Guid gameId,
        CancellationToken cancellationToken)
    {
        var replay = await history.PrivateReplayAsync(
            gameId,
            CurrentPlayer.GetId(User),
            cancellationToken);
        Response.Headers.CacheControl = "private, no-cache";
        Response.Headers.Vary = "Cookie";
        Response.Headers.ETag = replay.ETag;
        var cacheHit = Request.Headers.IfNoneMatch.Any(value =>
            StringComparer.Ordinal.Equals(value, replay.ETag));
        metrics.RecordReplayCacheValidation(cacheHit);
        if (cacheHit)
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Ok(replay.View);
    }

    [HttpPost("{gameId:guid}/replay-share", Name = "createReplayShare")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("share-change")]
    [ProducesResponseType<ReplayShareCreatedView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReplayShareCreatedView>> CreateReplayShare(
        Guid gameId,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "private, no-store";
        return Ok(await history.CreateShareAsync(
            gameId,
            CurrentPlayer.GetId(User),
            cancellationToken));
    }

    [HttpDelete("{gameId:guid}/replay-share", Name = "revokeReplayShare")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("share-change")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeReplayShare(
        Guid gameId,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "private, no-store";
        await history.RevokeShareAsync(
            gameId,
            CurrentPlayer.GetId(User),
            cancellationToken);
        return NoContent();
    }
}

[ApiController]
[Route("api/replay-shares")]
public sealed class ReplaySharesController(HistoryService history) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("{shareToken}", Name = "getSharedReplay")]
    [EnableRateLimiting("share-read")]
    [ProducesResponseType<HistoricalReplayView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<HistoricalReplayView>> Get(
        string shareToken,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "private, no-store";
        return Ok(await history.SharedReplayAsync(shareToken, cancellationToken));
    }
}
