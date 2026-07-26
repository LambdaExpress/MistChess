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
public sealed class GamesController(GameService games) : ControllerBase
{
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
    [ProducesResponseType<ReplayView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReplayView>> Replay(Guid gameId, CancellationToken cancellationToken) =>
        Ok(await games.ReplayAsync(gameId, CurrentPlayer.GetId(User), cancellationToken));
}
