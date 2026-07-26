using System.Globalization;
using System.Security.Cryptography;
using MistChess.Domain;
using MistChess.Infrastructure.Persistence;

namespace MistChess.Api.Application;

public sealed record TimeControlSettings(long InitialMilliseconds, long IncrementMilliseconds)
{
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Trim().Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var initialSeconds) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var incrementSeconds) ||
            initialSeconds is < 1 or > 86400 ||
            incrementSeconds is < 0 or > 3600)
        {
            throw ApiException.Unprocessable("INVALID_TIME_CONTROL", "Time control must use '<initialSeconds>+<incrementSeconds>'.");
        }

        return $"{initialSeconds.ToString(CultureInfo.InvariantCulture)}+{incrementSeconds.ToString(CultureInfo.InvariantCulture)}";
    }

    public static TimeControlSettings? Parse(string? value)
    {
        var normalized = Normalize(value);
        if (normalized is null)
        {
            return null;
        }

        var parts = normalized.Split('+');
        return new TimeControlSettings(
            long.Parse(parts[0], CultureInfo.InvariantCulture) * 1000,
            long.Parse(parts[1], CultureInfo.InvariantCulture) * 1000);
    }
}

public sealed class GameFactory(IGameStateSerializer stateSerializer, TimeProvider timeProvider)
{
    public GameEntity Create(Guid firstPlayerId, Guid secondPlayerId, string ruleVersion, string? timeControl)
    {
        if (!StringComparer.Ordinal.Equals(ruleVersion, GameState.CurrentRuleVersion))
        {
            throw ApiException.Unprocessable("UNSUPPORTED_RULE_VERSION", "The requested rule version is not supported.");
        }

        if (firstPlayerId == secondPlayerId)
        {
            throw new ArgumentException("A game requires two different players.", nameof(secondPlayerId));
        }

        var normalizedTimeControl = TimeControlSettings.Normalize(timeControl);
        var clock = TimeControlSettings.Parse(normalizedTimeControl);
        var firstIsRed = RandomNumberGenerator.GetInt32(2) == 0;
        var redPlayerId = firstIsRed ? firstPlayerId : secondPlayerId;
        var blackPlayerId = firstIsRed ? secondPlayerId : firstPlayerId;
        var now = timeProvider.GetUtcNow();
        var state = GameState.CreateInitial();
        var stateJson = stateSerializer.Serialize(state);
        var game = new GameEntity
        {
            Id = Guid.NewGuid(),
            RedPlayerId = redPlayerId,
            BlackPlayerId = blackPlayerId,
            InitialStateJson = stateJson,
            StateJson = stateJson,
            SideToMove = state.SideToMove,
            Status = GameStatus.Playing,
            RuleVersion = ruleVersion,
            TimeControl = normalizedTimeControl,
            RedMilliseconds = clock?.InitialMilliseconds,
            BlackMilliseconds = clock?.InitialMilliseconds,
            TurnStartedAt = clock is null ? null : now,
            Version = 0,
            CreatedAt = now,
            UpdatedAt = now
        };
        game.Players.Add(new GamePlayerEntity
        {
            GameId = game.Id,
            PlayerId = redPlayerId,
            Side = Side.Red,
            IsActive = true
        });
        game.Players.Add(new GamePlayerEntity
        {
            GameId = game.Id,
            PlayerId = blackPlayerId,
            Side = Side.Black,
            IsActive = true
        });
        return game;
    }

    public static Side GetSide(GameEntity game, Guid playerId)
    {
        if (game.RedPlayerId == playerId)
        {
            return Side.Red;
        }

        if (game.BlackPlayerId == playerId)
        {
            return Side.Black;
        }

        throw ApiException.NotFound();
    }

    public static void Finish(
        GameEntity game,
        RoomEntity? room,
        Side? winner,
        string reason,
        DateTimeOffset now)
    {
        game.Status = GameStatus.Finished;
        game.Winner = winner;
        game.ResultReason = reason;
        game.FinishedAt = now;
        game.UpdatedAt = now;
        game.TurnStartedAt = null;
        game.Version++;
        foreach (var participant in game.Players)
        {
            participant.IsActive = false;
        }

        if (room is not null)
        {
            room.Status = GameStatus.Finished;
            room.UpdatedAt = now;
        }
    }
}
