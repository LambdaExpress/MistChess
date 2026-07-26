using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using MistChess.Domain;

namespace MistChess.Api.Contracts;

public enum MatchTicketStatus
{
    Searching,
    Matched,
    Cancelled,
    Expired
}

public enum DrawOfferStatus
{
    Pending,
    Accepted,
    Rejected,
    Withdrawn
}

public enum GameResultReason
{
    GeneralCaptured,
    NoLegalMove,
    Resignation,
    Timeout,
    AgreedDraw,
    Repetition,
    NoProgress
}

public sealed record GuestSessionView(Guid PlayerId, string DisplayName);

public sealed record AntiforgeryTokenView(string Token, string HeaderName);

public sealed record RoomPlayerView(string DisplayName, Side? Side, bool IsReady, bool IsCurrentPlayer);

public sealed record RoomView(
    string Code,
    GameStatus Status,
    string RuleVersion,
    string? TimeControl,
    IReadOnlyList<RoomPlayerView> Players,
    Guid? GameId);

public sealed record CreateRoomRequest(
    [Required, MaxLength(64)][property: JsonRequired] string RuleVersion,
    [MaxLength(64)][property: JsonRequired] string? TimeControl);

public sealed record SetReadyRequest([property: JsonRequired] bool Ready);

public sealed record CreateMatchTicketRequest(
    [Required, MaxLength(64)][property: JsonRequired] string RuleVersion,
    [MaxLength(64)][property: JsonRequired] string? TimeControl,
    [Required, MinLength(1), MaxLength(64)][property: JsonRequired] string ClientRequestId);

public sealed record MatchTicketView(
    Guid TicketId,
    string RuleVersion,
    string? TimeControl,
    MatchTicketStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastHeartbeatAt,
    DateTimeOffset ExpiresAt,
    Guid? GameId);

public sealed record MatchFoundView(Guid TicketId, Guid GameId, Side Perspective);

public readonly record struct Position(
    [Required][property: JsonRequired] int File,
    [Required][property: JsonRequired] int Rank);

public sealed record PieceView(Side Side, PieceType Type, Position Position);

public sealed record CandidateMoveView(Position From, IReadOnlyList<Position> Destinations);

public sealed record CaptureSummaryView(
    IReadOnlyList<PieceType> RedLost,
    IReadOnlyList<PieceType> BlackLost);

public sealed record ClockView(long RedMilliseconds, long BlackMilliseconds, DateTimeOffset ServerTime);

public sealed record GameResultView(Side? Winner, GameResultReason Reason);

public sealed record GameView(
    Guid GameId,
    string RuleVersion,
    long Version,
    GameStatus Status,
    GameResultView? Result,
    Side Perspective,
    Side SideToMove,
    IReadOnlyList<Position> VisibleSquares,
    IReadOnlyList<PieceView> Pieces,
    IReadOnlyList<CandidateMoveView> CandidateMoves,
    CaptureSummaryView CaptureSummary,
    ClockView? Clock,
    DrawOfferView? DrawOffer);

public sealed record MoveRequest(
    [Required][property: JsonRequired] Position From,
    [Required][property: JsonRequired] Position To,
    [Range(0, long.MaxValue)][property: JsonRequired] long ExpectedVersion,
    [Required, MinLength(1), MaxLength(64)][property: JsonRequired] string ClientMoveId);

public sealed record DrawOfferView(DrawOfferStatus Status, Side OfferedBy);

public sealed record ReplayMoveView(
    int Ply,
    Side Side,
    PieceType Piece,
    Position From,
    Position To,
    PieceType? Captured);

public sealed record ReplayFrameView(
    int Ply,
    Side SideToMove,
    IReadOnlyList<PieceView> Pieces,
    ReplayMoveView? Move);

public sealed record ReplayView(
    Guid GameId,
    string RuleVersion,
    Side Perspective,
    GameResultView Result,
    IReadOnlyList<ReplayFrameView> Frames);

public sealed record ConnectionState(bool Connected);

public sealed record ErrorResponse(string Code, string Title, string? Detail = null, Guid? GameId = null);
