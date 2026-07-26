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

public sealed class GuestSessionEntity
{
    public Guid Id { get; set; }
    public required string TokenHash { get; set; }
    public required string DisplayName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
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
    public long? RedMilliseconds { get; set; }
    public long? BlackMilliseconds { get; set; }
    public DateTimeOffset? TurnStartedAt { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public List<MoveEntity> Moves { get; set; } = [];
    public List<MoveCommandReceiptEntity> MoveCommandReceipts { get; set; } = [];
    public List<DrawOfferEntity> DrawOffers { get; set; } = [];
    public List<GamePlayerEntity> Players { get; set; } = [];
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
    public required string ClientMoveId { get; set; }
    public required string PositionKey { get; set; }
    public required string StateAfterJson { get; set; }
    public long GameVersion { get; set; }
    public Side? WinnerAfter { get; set; }
    public string? ResultReasonAfter { get; set; }
    public long? RedMillisecondsAfter { get; set; }
    public long? BlackMillisecondsAfter { get; set; }
    public DateTimeOffset? TurnStartedAtAfter { get; set; }
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
