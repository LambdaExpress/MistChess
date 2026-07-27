using System.Globalization;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using MistChess.Domain;
using MistChess.Api.Contracts;
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

public static class GameOptionsCatalog
{
    public const string QuickMatchTimeControlId = "600+5";
    public const string DefaultRoomTimeControlId = "180+2";

    private static readonly TimeControlOptionView QuickMatchOption =
        new(QuickMatchTimeControlId, "10 分钟 + 5 秒", 600, 5);

    private static readonly IReadOnlyList<TimeControlOptionView> RoomOptions =
    [
        new("60+1", "1 分钟 + 1 秒", 60, 1),
        new("180+2", "3 分钟 + 2 秒", 180, 2),
        QuickMatchOption
    ];

    public static GameOptionsView View { get; } = new(
        GameState.CurrentRuleVersion,
        QuickMatchOption,
        RoomOptions,
        DefaultRoomTimeControlId,
        true);

    public static string? NormalizeRoomTimeControl(string? value)
    {
        var normalized = TimeControlSettings.Normalize(value);
        if (normalized is not null && RoomOptions.All(option => option.Id != normalized))
        {
            throw ApiException.Unprocessable(
                "UNSUPPORTED_ROOM_TIME_CONTROL",
                "The requested room time control is not available.");
        }

        return normalized;
    }
}


public sealed class GameFactory(IGameStateSerializer stateSerializer, TimeProvider timeProvider)
{
    public GameEntity Create(
        Guid firstPlayerId,
        Guid secondPlayerId,
        string ruleVersion,
        string? timeControl,
        bool isRated = false)
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
        if (isRated && normalizedTimeControl != GameOptionsCatalog.QuickMatchTimeControlId)
        {
            throw ApiException.Unprocessable(
                "INVALID_RATED_TIME_CONTROL",
                "Rated games must use the quick-match time control.");
        }
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
            IsRated = isRated,
            RedMilliseconds = clock?.InitialMilliseconds,
            BlackMilliseconds = clock?.InitialMilliseconds,
            TurnStartedAt = clock is null ? null : now,
            ClockExpiresAt = clock is null ? null : now.AddMilliseconds(clock.InitialMilliseconds),
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
        game.ClockExpiresAt = null;
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

public readonly record struct EloResult(int Before, int After)
{
    public int Delta => After - Before;
}
public sealed class MistChessMetrics : IDisposable
{
    public const string MeterName = "MistChess.Api";

    private readonly Meter meter = new(MeterName, "2.0.0");
    private readonly Counter<long> gameCompletions;
    private readonly Histogram<double> gameCompletionDuration;
    private readonly Counter<long> ratingSettlements;
    private readonly Histogram<int> ratingChanges;
    private readonly Counter<long> matchmakingScans;
    private readonly Histogram<long> eligiblePopulation;
    private readonly Histogram<double> matchmakingWaitingDuration;
    private readonly Counter<long> matchmakingTickets;
    private readonly Histogram<double> matchmakingTicketDuration;
    private readonly Counter<long> matches;
    private readonly Histogram<int> matchRatingDifference;
    private readonly Histogram<double> matchDuration;
    private readonly Counter<long> clockTimeouts;
    private readonly Histogram<double> clockScanDelay;
    private readonly Counter<long> clockDuplicateConflicts;
    private readonly Histogram<double> historyListDuration;
    private readonly Histogram<double> replayBuildDuration;
    private readonly Histogram<int> replayFrameCount;
    private readonly Histogram<long> replayResponseSize;
    private readonly Counter<long> replayCacheValidations;
    private readonly Counter<long> shareOperations;

    public MistChessMetrics()
    {
        gameCompletions = meter.CreateCounter<long>(
            "mistchess.game.completions",
            "{completion}",
            "Game completion attempts grouped by reason and idempotency outcome.");
        gameCompletionDuration = meter.CreateHistogram<double>(
            "mistchess.game.completion.duration",
            "ms",
            "Server-side game completion duration.");
        ratingSettlements = meter.CreateCounter<long>(
            "mistchess.rating.settlements",
            "{settlement}",
            "Rating settlement attempts grouped by created or reused outcome.");
        ratingChanges = meter.CreateHistogram<int>(
            "mistchess.rating.change",
            "{rating_point}",
            "Per-player rating delta for newly created settlements.");
        matchmakingScans = meter.CreateCounter<long>(
            "mistchess.matchmaking.scans",
            "{scan}",
            "Matchmaking scans grouped by population band and search restriction.");
        eligiblePopulation = meter.CreateHistogram<long>(
            "mistchess.matchmaking.eligible_population",
            "{player}",
            "Eligible waiting players observed by matchmaking scans.");
        matchmakingWaitingDuration = meter.CreateHistogram<double>(
            "mistchess.matchmaking.waiting.duration",
            "ms",
            "Anchor ticket waiting duration observed by matchmaking scans.");
        matchmakingTickets = meter.CreateCounter<long>(
            "mistchess.matchmaking.tickets",
            "{ticket}",
            "Matchmaking ticket lifecycle outcomes.");
        matchmakingTicketDuration = meter.CreateHistogram<double>(
            "mistchess.matchmaking.ticket.duration",
            "ms",
            "Ticket waiting duration grouped by terminal outcome.");
        matches = meter.CreateCounter<long>(
            "mistchess.matchmaking.matches",
            "{match}",
            "Created matches grouped by population band and unrestricted outcome.");
        matchRatingDifference = meter.CreateHistogram<int>(
            "mistchess.matchmaking.rating.difference",
            "{rating_point}",
            "Absolute rating difference for created matches.");
        matchDuration = meter.CreateHistogram<double>(
            "mistchess.matchmaking.match.duration",
            "ms",
            "Waiting duration before a match is created.");
        clockTimeouts = meter.CreateCounter<long>(
            "mistchess.clock.timeouts",
            "{timeout}",
            "Games completed by the background clock worker.");
        clockScanDelay = meter.CreateHistogram<double>(
            "mistchess.clock.scan.delay",
            "ms",
            "Delay between clock expiry and background completion.");
        clockDuplicateConflicts = meter.CreateCounter<long>(
            "mistchess.clock.duplicate_completion_conflicts",
            "{conflict}",
            "Clock expirations already completed by another request or worker.");
        historyListDuration = meter.CreateHistogram<double>(
            "mistchess.history.list.duration",
            "ms",
            "Private history list query duration.");
        replayBuildDuration = meter.CreateHistogram<double>(
            "mistchess.replay.build.duration",
            "ms",
            "Replay query and projection duration.");
        replayFrameCount = meter.CreateHistogram<int>(
            "mistchess.replay.frames",
            "{frame}",
            "Frames returned in a replay.");
        replayResponseSize = meter.CreateHistogram<long>(
            "mistchess.replay.response.size",
            "By",
            "Replay response size before or after response compression.");
        replayCacheValidations = meter.CreateCounter<long>(
            "mistchess.replay.cache.validations",
            "{validation}",
            "Private replay ETag validation outcomes.");
        shareOperations = meter.CreateCounter<long>(
            "mistchess.share.operations",
            "{operation}",
            "Replay share operations and read outcomes.");
    }

    public void RecordGameCompletion(
        string reason,
        string? timeControl,
        bool rated,
        bool created,
        double elapsedMilliseconds)
    {
        TagList tags = default;
        tags.Add("reason", reason);
        tags.Add("time_control", TimeControlTag(timeControl));
        tags.Add("rated", rated);
        tags.Add("outcome", created ? "created" : "idempotent");
        gameCompletions.Add(1, tags);
        gameCompletionDuration.Record(elapsedMilliseconds, tags);
    }

    public void RecordRatingSettlement(bool reused, int? redDelta = null, int? blackDelta = null)
    {
        TagList tags = default;
        tags.Add("outcome", reused ? "reused" : "created");
        ratingSettlements.Add(1, tags);
        if (reused || redDelta is null || blackDelta is null)
        {
            return;
        }

        TagList redTags = default;
        redTags.Add("side", "red");
        ratingChanges.Record(redDelta.Value, redTags);
        TagList blackTags = default;
        blackTags.Add("side", "black");
        ratingChanges.Record(blackDelta.Value, blackTags);
    }

    public void RecordMatchmakingScan(
        int population,
        MatchSearchRange range,
        double waitingMilliseconds,
        bool foundCandidate)
    {
        TagList tags = default;
        tags.Add("population_band", range.PopulationBand);
        tags.Add("unrestricted", range.IsUnrestricted);
        tags.Add("candidate", foundCandidate ? "found" : "none");
        matchmakingScans.Add(1, tags);
        eligiblePopulation.Record(population, tags);
        matchmakingWaitingDuration.Record(waitingMilliseconds, tags);
    }

    public void RecordMatchmakingTicket(string outcome, double? waitingMilliseconds)
    {
        TagList tags = default;
        tags.Add("outcome", outcome);
        matchmakingTickets.Add(1, tags);
        if (waitingMilliseconds is { } duration)
        {
            matchmakingTicketDuration.Record(Math.Max(0, duration), tags);
        }
    }

    public void RecordMatch(
        string populationBand,
        bool unrestricted,
        int ratingDifference,
        double elapsedMilliseconds)
    {
        TagList tags = default;
        tags.Add("population_band", populationBand);
        tags.Add("unrestricted", unrestricted);
        matches.Add(1, tags);
        matchRatingDifference.Record(ratingDifference, tags);
        matchDuration.Record(elapsedMilliseconds, tags);
    }

    public void RecordClockTimeout(string? timeControl, double delayMilliseconds, bool duplicate)
    {
        TagList tags = default;
        tags.Add("time_control", TimeControlTag(timeControl));
        tags.Add("outcome", duplicate ? "duplicate" : "completed");
        clockScanDelay.Record(delayMilliseconds, tags);
        if (duplicate)
        {
            clockDuplicateConflicts.Add(1, tags);
            return;
        }

        clockTimeouts.Add(1, tags);
    }

    public void RecordHistoryList(double elapsedMilliseconds)
    {
        historyListDuration.Record(elapsedMilliseconds);
    }

    public void RecordReplayBuild(bool shared, int frames, double elapsedMilliseconds)
    {
        TagList tags = default;
        tags.Add("access", shared ? "shared" : "private");
        replayBuildDuration.Record(elapsedMilliseconds, tags);
        replayFrameCount.Record(frames, tags);
    }

    public void RecordReplayResponseSize(bool compressed, long bytes, int statusCode)
    {
        TagList tags = default;
        tags.Add("representation", compressed ? "compressed" : "uncompressed");
        tags.Add("status_code", statusCode);
        replayResponseSize.Record(bytes, tags);
    }

    public void RecordReplayCacheValidation(bool hit)
    {
        TagList tags = default;
        tags.Add("outcome", hit ? "hit" : "miss");
        replayCacheValidations.Add(1, tags);
    }

    public void RecordShareOperation(string operation)
    {
        TagList tags = default;
        tags.Add("operation", operation);
        shareOperations.Add(1, tags);
    }

    public void Dispose() => meter.Dispose();

    private static string TimeControlTag(string? value) => value ?? "untimed";
}


public sealed class RatingService(ILogger<RatingService> logger, MistChessMetrics metrics)
{
    public const int InitialRating = 1500;
    public const int MinimumRating = 100;
    public const int ProvisionalGameCount = 20;

    public static EloResult Calculate(int rating, int opponentRating, int gamesPlayed, double score)
    {
        var expected = 1d / (1d + Math.Pow(10d, (opponentRating - rating) / 400d));
        var kFactor = gamesPlayed < ProvisionalGameCount ? 40 : 20;
        var updated = Math.Max(
            MinimumRating,
            (int)Math.Round(rating + (kFactor * (score - expected)), MidpointRounding.AwayFromZero));
        return new EloResult(rating, updated);
    }

    public async Task<RatingSettlementEntity?> SettleAsync(
        MistChessDbContext db,
        GameEntity game,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!game.IsRated)
        {
            return null;
        }

        var existing = await db.RatingSettlements.SingleOrDefaultAsync(
            value => value.GameId == game.Id,
            cancellationToken);
        if (existing is not null)
        {
            game.RatingSettlement = existing;
            logger.LogInformation("Rating settlement reused gameId={GameId}", game.Id);
            metrics.RecordRatingSettlement(reused: true);
            return existing;
        }

        if (game.Status != GameStatus.Finished ||
            game.TimeControl != GameOptionsCatalog.QuickMatchTimeControlId)
        {
            throw new InvalidOperationException("Only finished rated quick-match games can be settled.");
        }

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO player_ratings
                (player_id, rule_version, time_control, rating, games_played, wins, draws, losses, updated_at, concurrency_stamp)
            VALUES
                ({game.RedPlayerId}, {game.RuleVersion}, {game.TimeControl}, {InitialRating}, 0, 0, 0, 0, {now}, 0),
                ({game.BlackPlayerId}, {game.RuleVersion}, {game.TimeControl}, {InitialRating}, 0, 0, 0, 0, {now}, 0)
            ON CONFLICT (player_id, rule_version, time_control) DO NOTHING
            """,
            cancellationToken);

        var ratings = await db.PlayerRatings
            .FromSqlInterpolated(
                $"""
                SELECT *
                FROM player_ratings
                WHERE player_id IN ({game.RedPlayerId}, {game.BlackPlayerId})
                  AND rule_version = {game.RuleVersion}
                  AND time_control = {game.TimeControl}
                ORDER BY player_id
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);
        var red = ratings.Single(value => value.PlayerId == game.RedPlayerId);
        var black = ratings.Single(value => value.PlayerId == game.BlackPlayerId);
        var redScore = game.Winner switch
        {
            Side.Red => 1d,
            Side.Black => 0d,
            null => 0.5d,
            _ => throw new InvalidDataException("A rated game has an invalid winner.")
        };
        var blackScore = 1d - redScore;
        var redResult = Calculate(red.Rating, black.Rating, red.GamesPlayed, redScore);
        var blackResult = Calculate(black.Rating, red.Rating, black.GamesPlayed, blackScore);

        ApplyResult(red, redResult.After, redScore, now);
        ApplyResult(black, blackResult.After, blackScore, now);

        var settlement = new RatingSettlementEntity
        {
            GameId = game.Id,
            RedRatingBefore = redResult.Before,
            RedRatingAfter = redResult.After,
            BlackRatingBefore = blackResult.Before,
            BlackRatingAfter = blackResult.After,
            RedScore = (decimal)redScore,
            SettledAt = now
        };
        db.RatingSettlements.Add(settlement);
        game.RatingSettlement = settlement;
        logger.LogInformation(
            "Rating settled gameId={GameId} redBefore={RedBefore} redAfter={RedAfter} blackBefore={BlackBefore} blackAfter={BlackAfter}",
            game.Id,
            redResult.Before,
            redResult.After,
            blackResult.Before,
            blackResult.After);
        metrics.RecordRatingSettlement(
            reused: false,
            redResult.Delta,
            blackResult.Delta);
        return settlement;
    }

    private static void ApplyResult(
        PlayerRatingEntity rating,
        int updatedRating,
        double score,
        DateTimeOffset now)
    {
        rating.Rating = updatedRating;
        rating.GamesPlayed++;
        if (score == 1d)
        {
            rating.Wins++;
        }
        else if (score == 0d)
        {
            rating.Losses++;
        }
        else
        {
            rating.Draws++;
        }

        rating.UpdatedAt = now;
        rating.ConcurrencyStamp++;
    }
}

public sealed class GameCompletionService(RatingService ratings, MistChessMetrics metrics)
{
    public async Task<bool> CompleteAsync(
        MistChessDbContext db,
        GameEntity game,
        RoomEntity? room,
        Side? winner,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var created = game.Status != GameStatus.Finished;
        if (created)
        {
            GameFactory.Finish(game, room, winner, reason, now);
        }

        await ratings.SettleAsync(db, game, now, cancellationToken);
        metrics.RecordGameCompletion(
            reason,
            game.TimeControl,
            game.IsRated,
            created,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return created;
    }
}
