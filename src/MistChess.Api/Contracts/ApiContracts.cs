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

public enum TakebackRequestStatus
{
    Pending,
    Accepted,
    Rejected,
    Withdrawn
}

public enum GameActionKind
{
    Move,
    Capture,
    TakebackAccepted
}

public enum GameResultReason
{
    GeneralCaptured,
    NoLegalMove,
    Resignation,
    Timeout,
    AgreedDraw,
    Repetition,
    NoProgress,
    AdministrativeForfeit
}


public sealed record GuestSessionView(
    Guid PlayerId,
    string DisplayName,
    Guid? ActiveGameId);

public sealed record AntiforgeryTokenView(string Token, string HeaderName);
public sealed record AdminLoginRequest(
    [Required, MinLength(1), MaxLength(64)][property: JsonRequired] string Username,
    [Required, MinLength(1), MaxLength(1024)][property: JsonRequired] string Password);

public sealed record AdminSessionView(string Username, DateTimeOffset ExpiresAt);

public sealed record AdminRatingView(
    string RuleVersion,
    string TimeControl,
    int Rating,
    int GamesPlayed,
    int Wins,
    int Draws,
    int Losses,
    decimal? WinRate,
    DateTimeOffset UpdatedAt);

public sealed record AdminUserSummaryView(
    Guid PlayerId,
    string DisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset LastSeenAt,
    bool Online,
    bool Banned,
    DateTimeOffset? BannedAt,
    string? BanReason,
    string? BannedBy,
    int Rating,
    int GamesPlayed,
    int Wins,
    int Draws,
    int Losses,
    decimal? WinRate);

public sealed record AdminUsersPageView(
    IReadOnlyList<AdminUserSummaryView> Items,
    string? NextCursor,
    DateTimeOffset ObservedAt);

public sealed record AdminUserDetailView(
    AdminUserSummaryView User,
    IReadOnlyList<AdminRatingView> Ratings,
    DateTimeOffset ObservedAt);

public sealed record AdminBanRequest(
    [Required, MinLength(1), MaxLength(200)][property: JsonRequired] string Reason);

public sealed record AdminBanStatusView(
    Guid PlayerId,
    bool Banned,
    DateTimeOffset? BannedAt,
    string? BanReason,
    string? BannedBy);

public sealed record AccountBannedView(string Reason);

public sealed record TimeControlOptionView(
    string Id,
    string Label,
    int InitialSeconds,
    int IncrementSeconds);
public sealed record MoveTimeLimitOptionView(int Seconds, string Label);


public sealed record GameOptionsView(
    string RuleVersion,
    TimeControlOptionView QuickMatchTimeControl,
    IReadOnlyList<TimeControlOptionView> RoomTimeControls,
    string DefaultRoomTimeControlId,
    bool AllowUntimedRooms,
    int QuickMatchMoveTimeLimitSeconds,
    IReadOnlyList<MoveTimeLimitOptionView> RoomMoveTimeLimits,
    int DefaultRoomMoveTimeLimitSeconds);

public sealed record RoomPlayerView(string DisplayName, Side? Side, bool IsReady, bool IsCurrentPlayer);

public sealed record RoomView(
    string Code,
    GameStatus Status,
    string RuleVersion,
    string? TimeControl,
    IReadOnlyList<RoomPlayerView> Players,
    Guid? GameId,
    int? MoveTimeLimitSeconds = null);

public sealed record CreateRoomRequest(
    [Required, MaxLength(64)][property: JsonRequired] string RuleVersion,
    [MaxLength(64)][property: JsonRequired] string? TimeControl,
    int? MoveTimeLimitSeconds = null);

public sealed record SetReadyRequest([property: JsonRequired] bool Ready);

public sealed record CreateMatchTicketRequest(
    [Required, MaxLength(64)][property: JsonRequired] string RuleVersion,
    [Required, MinLength(1), MaxLength(64)][property: JsonRequired] string ClientRequestId);

public sealed record MatchTicketView(
    Guid TicketId,
    string RuleVersion,
    string? TimeControl,
    MatchTicketStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastHeartbeatAt,
    DateTimeOffset ExpiresAt,
    Guid? GameId,
    int? MoveTimeLimitSeconds = null);

public sealed record MatchFoundView(Guid TicketId, Guid GameId, Side Perspective);

public readonly record struct Position(
    [Required][property: JsonRequired] int File,
    [Required][property: JsonRequired] int Rank);

public sealed record PieceView(Side Side, PieceType Type, Position Position);

public sealed record CandidateMoveView(Position From, IReadOnlyList<Position> Destinations);

public sealed record CaptureSummaryView(
    IReadOnlyList<PieceType> RedLost,
    IReadOnlyList<PieceType> BlackLost);

public sealed record ClockView(
    long RedMilliseconds,
    long BlackMilliseconds,
    DateTimeOffset ServerTime,
    long? TurnMilliseconds = null);

public sealed record GameResultView(Side? Winner, GameResultReason Reason);

public sealed record GameView(
    Guid GameId,
    string RuleVersion,
    string? TimeControl,
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
    DrawOfferView? DrawOffer,
    long NegotiationVersion,
    TakebackRequestView? TakebackRequest,
    GameActionView? LastAction,
    bool CanRequestTakeback,
    int? MoveTimeLimitSeconds = null);

public sealed record MoveRequest(
    [Required][property: JsonRequired] Position From,
    [Required][property: JsonRequired] Position To,
    [Range(0, long.MaxValue)][property: JsonRequired] long ExpectedVersion,
    [Required, MinLength(1), MaxLength(64)][property: JsonRequired] string ClientMoveId);

public sealed record CreateTakebackRequest(
    [Range(0, long.MaxValue)][property: JsonRequired] long ExpectedVersion,
    [Required, MinLength(1), MaxLength(64)][property: JsonRequired] string ClientRequestId);

public sealed record DrawOfferView(Guid Id, DrawOfferStatus Status, Side OfferedBy, long Revision);

public sealed record TakebackRequestView(
    Guid Id,
    TakebackRequestStatus Status,
    Side RequestedBy,
    int RequestedPly,
    long RequestedAtVersion,
    long? ResolvedAtVersion,
    DateTimeOffset CreatedAt,
    long Revision);

public sealed record GameActionView(long Version, GameActionKind Kind, Side Actor);

public sealed record ReplayMoveView(
    int Ply,
    Side Side,
    PieceType Piece,
    Position From,
    Position To,
    PieceType? Captured);

public enum HistoricalOutcome
{
    Win,
    Loss,
    Draw
}

public sealed record HistoricalPlayerView(
    string DisplayName,
    HistoricalOutcome Outcome);

public sealed record HistoricalGameSummaryView(
    Guid GameId,
    DateTimeOffset FinishedAt,
    string RuleVersion,
    string? TimeControl,
    Side CurrentPlayerSide,
    HistoricalPlayerView Red,
    HistoricalPlayerView Black,
    GameResultView Result,
    int PlyCount,
    int? MoveTimeLimitSeconds = null);

public sealed record HistoricalGamesPageView(
    IReadOnlyList<HistoricalGameSummaryView> Games,
    string? NextCursor);

public sealed record AdminHistoricalGameSummaryView(
    Guid GameId,
    DateTimeOffset FinishedAt,
    string RuleVersion,
    string? TimeControl,
    Side CurrentPlayerSide,
    HistoricalPlayerView Red,
    HistoricalPlayerView Black,
    GameResultView Result,
    int PlyCount,
    int? MoveTimeLimitSeconds,
    bool IsRated);

public sealed record AdminHistoricalGamesPageView(
    IReadOnlyList<AdminHistoricalGameSummaryView> Games,
    string? NextCursor);

public sealed record ReplayFrameProjectionView(
    IReadOnlyList<Position> VisibleSquares,
    IReadOnlyList<PieceView> Pieces,
    CaptureSummaryView CaptureSummary,
    ReplayMoveView? Move);

public sealed record ReplayFrameViewsView(
    ReplayFrameProjectionView Red,
    ReplayFrameProjectionView Black,
    ReplayFrameProjectionView Omniscient);

public sealed record HistoricalReplayFrameView(
    int Ply,
    Side SideToMove,
    ClockView? Clock,
    ReplayFrameViewsView Views);

public sealed record HistoricalReplayView(
    Guid GameId,
    string RuleVersion,
    string? TimeControl,
    Side? CurrentPlayerSide,
    HistoricalPlayerView Red,
    HistoricalPlayerView Black,
    GameResultView Result,
    IReadOnlyList<HistoricalReplayFrameView> Frames,
    int? MoveTimeLimitSeconds = null);

public sealed record ReplayShareCreatedView(
    string SharePath,
    DateTimeOffset CreatedAt);

public sealed record ConnectionState(bool Connected);

public sealed record ErrorResponse(string Code, string Title, string? Detail = null, Guid? GameId = null);
