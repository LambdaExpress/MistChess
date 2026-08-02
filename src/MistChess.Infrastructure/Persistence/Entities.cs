using MistChess.Domain;

namespace MistChess.Infrastructure.Persistence;

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

public sealed class GuestSessionEntity
{
    public Guid Id { get; set; }
    public required string TokenHash { get; set; }
    public required string DisplayName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool IsBanned { get; set; }
    public DateTimeOffset? BannedAt { get; set; }
    public string? BanReason { get; set; }
    public string? BannedBy { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

public sealed class RoomEntity
{
    public Guid Id { get; set; }
    public required string Code { get; set; }
    public Guid CreatorPlayerId { get; set; }
    public GuestSessionEntity CreatorPlayer { get; set; } = null!;
    public GameStatus Status { get; set; }
    public required string RuleVersion { get; set; }
    public string? TimeControl { get; set; }
    public long? MoveTimeLimitMilliseconds { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? GameId { get; set; }
    public GameEntity? Game { get; set; }
    public List<RoomPlayerEntity> Players { get; set; } = [];
}

public sealed class RoomPlayerEntity
{
    public Guid RoomId { get; set; }
    public RoomEntity Room { get; set; } = null!;
    public Guid PlayerId { get; set; }
    public GuestSessionEntity Player { get; set; } = null!;
    public byte Seat { get; set; }
    public Side? Side { get; set; }
    public bool IsReady { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
}

public sealed class MatchmakingTicketEntity
{
    public Guid Id { get; set; }
    public Guid PlayerId { get; set; }
    public GuestSessionEntity Player { get; set; } = null!;
    public required string RuleVersion { get; set; }
    public string? TimeControl { get; set; }
    public long? MoveTimeLimitMilliseconds { get; set; }
    public int RatingSnapshot { get; set; }
    public MatchTicketStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastHeartbeatAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public required string ClientRequestId { get; set; }
    public Guid? GameId { get; set; }
    public GameEntity? Game { get; set; }
    public int ConcurrencyStamp { get; set; }
}

public sealed class GameEntity
{
    public Guid Id { get; set; }
    public Guid RedPlayerId { get; set; }
    public GuestSessionEntity RedPlayer { get; set; } = null!;
    public Guid BlackPlayerId { get; set; }
    public GuestSessionEntity BlackPlayer { get; set; } = null!;
    public required string InitialStateJson { get; set; }
    public required string StateJson { get; set; }
    public Side SideToMove { get; set; }
    public GameStatus Status { get; set; }
    public Side? Winner { get; set; }
    public string? ResultReason { get; set; }
    public required string RuleVersion { get; set; }
    public string? TimeControl { get; set; }
    public long? MoveTimeLimitMilliseconds { get; set; }
    public long? TurnMilliseconds { get; set; }
    public bool IsRated { get; set; }
    public long? RedMilliseconds { get; set; }
    public long? BlackMilliseconds { get; set; }
    public DateTimeOffset? TurnStartedAt { get; set; }
    public DateTimeOffset? ClockExpiresAt { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public long? LastActionVersion { get; set; }
    public string? LastActionKind { get; set; }
    public Side? LastActionActor { get; set; }
    public long NegotiationVersion { get; set; }
    public bool TakebackWindowConsumed { get; set; }
    public List<MoveEntity> Moves { get; set; } = [];
    public List<MoveCommandReceiptEntity> MoveCommandReceipts { get; set; } = [];
    public List<DrawOfferEntity> DrawOffers { get; set; } = [];
    public List<TakebackRequestEntity> TakebackRequests { get; set; } = [];
    public List<GamePlayerEntity> Players { get; set; } = [];
    public RatingSettlementEntity? RatingSettlement { get; set; }
    public List<ReplayShareEntity> ReplayShares { get; set; } = [];
}

public sealed class GamePlayerEntity
{
    public Guid GameId { get; set; }
    public GameEntity Game { get; set; } = null!;
    public Guid PlayerId { get; set; }
    public GuestSessionEntity Player { get; set; } = null!;
    public Side Side { get; set; }
    public bool IsActive { get; set; }
}

public sealed class MoveEntity
{
    public Guid Id { get; set; }
    public Guid GameId { get; set; }
    public GameEntity Game { get; set; } = null!;
    public int Ply { get; set; }
    public byte FromFile { get; set; }
    public byte FromRank { get; set; }
    public byte ToFile { get; set; }
    public byte ToRank { get; set; }
    public Side Side { get; set; }
    public PieceType MovingPieceType { get; set; }
    public PieceType? CapturedPieceType { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public long? TurnMillisecondsBefore { get; set; }
    public required string ClientMoveId { get; set; }
    public required string PositionKey { get; set; }
    public required string StateAfterJson { get; set; }
    public long GameVersion { get; set; }
    public Side? WinnerAfter { get; set; }
    public string? ResultReasonAfter { get; set; }
    public long? RedMillisecondsAfter { get; set; }
    public long? BlackMillisecondsAfter { get; set; }
    public DateTimeOffset? TurnStartedAtAfter { get; set; }
    public long? TurnMillisecondsAfter { get; set; }
    public DateTimeOffset? RevertedAt { get; set; }
    public Guid? RevertedByTakebackRequestId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class MoveCommandReceiptEntity
{
    public Guid Id { get; set; }
    public Guid GameId { get; set; }
    public GameEntity Game { get; set; } = null!;
    public Guid PlayerId { get; set; }
    public GuestSessionEntity Player { get; set; } = null!;
    public required string ClientMoveId { get; set; }
    public required string StateAfterJson { get; set; }
    public long GameVersion { get; set; }
    public Side? WinnerAfter { get; set; }
    public string? ResultReasonAfter { get; set; }
    public long? RedMillisecondsAfter { get; set; }
    public long? BlackMillisecondsAfter { get; set; }
    public DateTimeOffset? TurnStartedAtAfter { get; set; }
    public long? TurnMillisecondsAfter { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class DrawOfferEntity
{
    public Guid Id { get; set; }
    public Guid GameId { get; set; }
    public GameEntity Game { get; set; } = null!;
    public Guid OfferedByPlayerId { get; set; }
    public GuestSessionEntity OfferedByPlayer { get; set; } = null!;
    public DrawOfferStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class TakebackRequestEntity
{
    public Guid Id { get; set; }
    public Guid GameId { get; set; }
    public GameEntity Game { get; set; } = null!;
    public Guid RequestedByPlayerId { get; set; }
    public GuestSessionEntity RequestedByPlayer { get; set; } = null!;
    public Guid MoveId { get; set; }
    public MoveEntity Move { get; set; } = null!;
    public int RequestedPly { get; set; }
    public long RequestedAtVersion { get; set; }
    public long? ResolvedAtVersion { get; set; }
    public required string ClientRequestId { get; set; }
    public TakebackRequestStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class PlayerRatingEntity
{
    public Guid PlayerId { get; set; }
    public GuestSessionEntity Player { get; set; } = null!;
    public required string RuleVersion { get; set; }
    public required string TimeControl { get; set; }
    public int Rating { get; set; }
    public int GamesPlayed { get; set; }
    public int Wins { get; set; }
    public int Draws { get; set; }
    public int Losses { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int ConcurrencyStamp { get; set; }
}

public sealed class RatingSettlementEntity
{
    public Guid GameId { get; set; }
    public GameEntity Game { get; set; } = null!;
    public int RedRatingBefore { get; set; }
    public int RedRatingAfter { get; set; }
    public int BlackRatingBefore { get; set; }
    public int BlackRatingAfter { get; set; }
    public decimal RedScore { get; set; }
    public DateTimeOffset SettledAt { get; set; }
}

public sealed class ReplayShareEntity
{
    public Guid Id { get; set; }
    public Guid GameId { get; set; }
    public GameEntity Game { get; set; } = null!;
    public Guid OwnerPlayerId { get; set; }
    public GuestSessionEntity OwnerPlayer { get; set; } = null!;
    public required string TokenHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
