using System.Data;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using MistChess.Api.Contracts;
using MistChess.Domain;
using MistChess.Infrastructure.Persistence;

namespace MistChess.Api.Application;

public sealed class RoomService(
    MistChessDbContext db,
    GameFactory gameFactory,
    TimeProvider timeProvider)
{
    private const string RoomAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public async Task<RoomView> CreateAsync(
        Guid playerId,
        CreateRoomRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRuleVersion(request.RuleVersion);
        var timeControl = GameOptionsCatalog.NormalizeRoomTimeControl(request.TimeControl);
        var moveTimeLimit = GameOptionsCatalog.NormalizeRoomMoveTimeLimit(
            timeControl,
            request.MoveTimeLimitSeconds);
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await LockPlayerForMutationAsync(playerId, cancellationToken);
        await EnsurePlayerCanStartGameAsync(playerId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var room = new RoomEntity
        {
            Id = Guid.NewGuid(),
            Code = CreateRoomCode(),
            CreatorPlayerId = playerId,
            Status = GameStatus.WaitingForOpponent,
            RuleVersion = request.RuleVersion,
            TimeControl = timeControl,
            MoveTimeLimitMilliseconds = moveTimeLimit,
            CreatedAt = now,
            UpdatedAt = now
        };
        room.Players.Add(new RoomPlayerEntity
        {
            RoomId = room.Id,
            PlayerId = playerId,
            Seat = 0,
            IsReady = false,
            JoinedAt = now
        });
        db.Rooms.Add(room);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await LoadViewAsync(room.Code, playerId, cancellationToken);
    }

    public async Task<RoomView> JoinAsync(string code, Guid playerId, CancellationToken cancellationToken)
    {
        code = NormalizeCode(code);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await LockPlayerForMutationAsync(playerId, cancellationToken);
        var room = await db.Rooms
            .Include(value => value.Players)
            .ThenInclude(value => value.Player)
            .SingleOrDefaultAsync(value => value.Code == code, cancellationToken)
            ?? throw ApiException.NotFound();

        var existing = room.Players.SingleOrDefault(value => value.PlayerId == playerId);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return ToView(room, playerId);
        }

        if (room.Status != GameStatus.WaitingForOpponent || room.Players.Count != 1)
        {
            throw ApiException.NotFound();
        }

        await EnsurePlayerCanStartGameAsync(playerId, cancellationToken);
        room.Players.Add(new RoomPlayerEntity
        {
            RoomId = room.Id,
            PlayerId = playerId,
            Seat = 1,
            IsReady = false,
            JoinedAt = timeProvider.GetUtcNow()
        });
        room.Status = GameStatus.WaitingForReady;
        room.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await LoadViewAsync(room.Code, playerId, cancellationToken);
    }

    public async Task LeaveAsync(string code, Guid playerId, CancellationToken cancellationToken)
    {
        code = NormalizeCode(code);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var lockedRooms = await db.Rooms
            .FromSqlInterpolated($"SELECT * FROM rooms WHERE code = {code} FOR UPDATE")
            .ToListAsync(cancellationToken);
        var room = lockedRooms.SingleOrDefault() ?? throw ApiException.NotFound();
        await db.Entry(room).Collection(value => value.Players).LoadAsync(cancellationToken);
        var player = room.Players.SingleOrDefault(value => value.PlayerId == playerId)
            ?? throw ApiException.NotFound();

        if (room.Status is GameStatus.Playing or GameStatus.Finished)
        {
            throw ApiException.Conflict("ROOM_ALREADY_STARTED", "A started room cannot be left.");
        }

        if (room.CreatorPlayerId == playerId)
        {
            db.Rooms.Remove(room);
        }
        else
        {
            db.RoomPlayers.Remove(player);
            foreach (var remainingPlayer in room.Players.Where(value => value.PlayerId != playerId))
            {
                remainingPlayer.IsReady = false;
            }

            room.Status = GameStatus.WaitingForOpponent;
            room.UpdatedAt = timeProvider.GetUtcNow();
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<RoomView> SetReadyAsync(
        string code,
        Guid playerId,
        bool ready,
        CancellationToken cancellationToken)
    {
        code = NormalizeCode(code);
        var expectedParticipantIds = ready
            ? await db.RoomPlayers
                .AsNoTracking()
                .Where(member => member.Room.Code == code)
                .OrderBy(member => member.PlayerId)
                .Select(member => member.PlayerId)
                .ToArrayAsync(cancellationToken)
            : [];
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        List<GuestSessionEntity> lockedPlayers = [];
        if (expectedParticipantIds.Length == 2)
        {
            lockedPlayers = await db.GuestSessions
                .FromSqlInterpolated(
                    $"""
                    SELECT *
                    FROM guest_sessions
                    WHERE id IN ({expectedParticipantIds[0]}, {expectedParticipantIds[1]})
                    ORDER BY id
                    FOR UPDATE
                    """)
                .ToListAsync(cancellationToken);
            if (lockedPlayers.Count != expectedParticipantIds.Length)
            {
                throw ApiException.NotFound();
            }

            var currentPlayer = lockedPlayers.SingleOrDefault(value => value.Id == playerId);
            if (currentPlayer?.IsBanned == true)
            {
                throw ApiException.PlayerBanned(currentPlayer.BanReason);
            }
        }

        var lockedRooms = await db.Rooms
            .FromSqlInterpolated($"SELECT * FROM rooms WHERE code = {code} FOR UPDATE")
            .ToListAsync(cancellationToken);
        var room = lockedRooms.SingleOrDefault() ?? throw ApiException.NotFound();
        await db.Entry(room)
            .Collection(value => value.Players)
            .Query()
            .Include(value => value.Player)
            .LoadAsync(cancellationToken);
        var player = room.Players.SingleOrDefault(value => value.PlayerId == playerId)
            ?? throw ApiException.NotFound();
        if (room.Status == GameStatus.Playing && room.GameId is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return ToView(room, playerId);
        }

        if (room.Status != GameStatus.WaitingForReady || room.Players.Count != 2)
        {
            throw ApiException.Conflict("ROOM_NOT_READY", "The room is not ready to start.");
        }

        player.IsReady = ready;
        room.UpdatedAt = timeProvider.GetUtcNow();
        if (room.Players.All(value => value.IsReady))
        {
            var participantIds = room.Players
                .Select(value => value.PlayerId)
                .OrderBy(value => value)
                .ToArray();
            if (lockedPlayers.Count != participantIds.Length ||
                !lockedPlayers.Select(value => value.Id).SequenceEqual(participantIds))
            {
                throw ApiException.Conflict(
                    "ROOM_MEMBERS_CHANGED",
                    "The room membership changed while the game was starting.");
            }

            foreach (var participant in room.Players)
            {
                await EnsurePlayerCanStartGameAsync(participant.PlayerId, cancellationToken);
            }

            var game = gameFactory.Create(
                room.Players[0].PlayerId,
                room.Players[1].PlayerId,
                room.RuleVersion,
                room.TimeControl,
                room.MoveTimeLimitMilliseconds);
            db.Games.Add(game);
            foreach (var participant in room.Players)
            {
                participant.Side = GameFactory.GetSide(game, participant.PlayerId);
            }

            room.GameId = game.Id;
            room.Status = GameStatus.Playing;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToView(room, playerId);
    }

    private async Task<RoomView> LoadViewAsync(string code, Guid playerId, CancellationToken cancellationToken)
    {
        var room = await db.Rooms
            .AsNoTracking()
            .Include(value => value.Players)
            .ThenInclude(value => value.Player)
            .SingleAsync(value => value.Code == code, cancellationToken);
        return ToView(room, playerId);
    }

    private async Task<GuestSessionEntity> LockPlayerForMutationAsync(
        Guid playerId,
        CancellationToken cancellationToken)
    {
        var lockedPlayers = await db.GuestSessions
            .FromSqlInterpolated(
                $"SELECT * FROM guest_sessions WHERE id = {playerId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        var player = lockedPlayers.SingleOrDefault() ?? throw ApiException.NotFound();
        if (player.IsBanned)
        {
            throw ApiException.PlayerBanned(player.BanReason);
        }

        return player;
    }

    private async Task EnsurePlayerCanStartGameAsync(Guid playerId, CancellationToken cancellationToken)
    {
        if (await db.GamePlayers.AnyAsync(value => value.PlayerId == playerId && value.IsActive, cancellationToken))
        {
            throw ApiException.Conflict("ACTIVE_GAME_EXISTS", "The player already has an unfinished game.");
        }

        var now = timeProvider.GetUtcNow();
        if (await db.MatchmakingTickets.AnyAsync(
                value => value.PlayerId == playerId &&
                    value.Status == MistChess.Infrastructure.Persistence.MatchTicketStatus.Searching &&
                    value.ExpiresAt > now,
                cancellationToken))
        {
            throw ApiException.Conflict("ACTIVE_TICKET_EXISTS", "Cancel the active matchmaking ticket first.");
        }
    }

    private static RoomView ToView(RoomEntity room, Guid currentPlayerId) => new(
        room.Code,
        room.Status,
        room.RuleVersion,
        room.TimeControl,
        room.Players
            .OrderBy(value => value.Seat)
            .Select(value => new RoomPlayerView(
                value.Player.DisplayName,
                value.Side,
                value.IsReady,
                value.PlayerId == currentPlayerId))
            .ToArray(),
        room.GameId,
        ToSeconds(room.MoveTimeLimitMilliseconds));

    private static int? ToSeconds(long? milliseconds) =>
        milliseconds is null ? null : checked((int)(milliseconds.Value / 1000));

    private static string NormalizeCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw ApiException.NotFound();
        }

        return code.Trim().ToUpperInvariant();
    }

    private static string CreateRoomCode()
    {
        Span<char> code = stackalloc char[8];
        for (var index = 0; index < code.Length; index++)
        {
            code[index] = RoomAlphabet[RandomNumberGenerator.GetInt32(RoomAlphabet.Length)];
        }

        return new string(code);
    }

    private static void ValidateRuleVersion(string ruleVersion)
    {
        if (!StringComparer.Ordinal.Equals(ruleVersion, GameState.CurrentRuleVersion))
        {
            throw ApiException.Unprocessable("UNSUPPORTED_RULE_VERSION", "The requested rule version is not supported.");
        }
    }
}
