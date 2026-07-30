using System.Data;
using Microsoft.EntityFrameworkCore;
using MistChess.Api.Contracts;
using MistChess.Domain;
using MistChess.Infrastructure.Persistence;
using Npgsql;
using ApiTicketStatus = MistChess.Api.Contracts.MatchTicketStatus;
using DbTicketStatus = MistChess.Infrastructure.Persistence.MatchTicketStatus;

namespace MistChess.Api.Application;

public sealed class MatchmakingService(
    MistChessDbContext db,
    MatchmakingCoordinator coordinator,
    ILobbyNotifier lobbyNotifier,
    TimeProvider timeProvider,
    MistChessMetrics metrics)
{
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(90);
    private const int MaxTransactionAttempts = 3;

    public async Task<MatchTicketView> CreateAsync(
        Guid playerId,
        CreateMatchTicketRequest request,
        CancellationToken cancellationToken)
    {
        if (!StringComparer.Ordinal.Equals(request.RuleVersion, GameState.CurrentRuleVersion))
        {
            throw ApiException.Unprocessable("UNSUPPORTED_RULE_VERSION", "The requested rule version is not supported.");
        }

        var requestId = request.ClientRequestId.Trim();
        if (requestId.Length is < 1 or > 64)
        {
            throw ApiException.Unprocessable("INVALID_CLIENT_REQUEST_ID", "A clientRequestId is required.");
        }

        const string timeControl = GameOptionsCatalog.QuickMatchTimeControlId;
        MatchmakingTicketEntity? created = null;
        for (var attempt = 1; attempt <= MaxTransactionAttempts; attempt++)
        {
            try
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(
                    IsolationLevel.ReadCommitted,
                    cancellationToken);
                var lockedPlayers = await db.GuestSessions
                    .FromSqlInterpolated($"SELECT * FROM guest_sessions WHERE id = {playerId} FOR UPDATE")
                    .ToListAsync(cancellationToken);
                if (lockedPlayers.Count == 0)
                {
                    throw ApiException.NotFound();
                }
                if (lockedPlayers[0].IsBanned)
                {
                    throw ApiException.PlayerBanned(lockedPlayers[0].BanReason);
                }

                var idempotent = await db.MatchmakingTickets
                    .SingleOrDefaultAsync(
                        value => value.PlayerId == playerId && value.ClientRequestId == requestId,
                        cancellationToken);
                if (idempotent is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return ToView(idempotent);
                }

                var now = timeProvider.GetUtcNow();
                var expired = await db.MatchmakingTickets
                    .Where(value =>
                        value.PlayerId == playerId &&
                        value.Status == DbTicketStatus.Searching &&
                        value.ExpiresAt <= now)
                    .ToListAsync(cancellationToken);
                foreach (var ticket in expired)
                {
                    ticket.Status = DbTicketStatus.Expired;
                    ticket.ConcurrencyStamp++;
                }

                if (expired.Count > 0)
                {
                    await db.SaveChangesAsync(cancellationToken);
                }

                var activeGameId = await db.GamePlayers
                    .Where(value => value.PlayerId == playerId && value.IsActive)
                    .Select(value => (Guid?)value.GameId)
                    .SingleOrDefaultAsync(cancellationToken);
                if (activeGameId is not null)
                {
                    throw ApiException.Conflict(
                        "ACTIVE_GAME_EXISTS",
                        "The player already has an unfinished game.",
                        activeGameId);
                }

                if (await db.MatchmakingTickets.AnyAsync(
                        value => value.PlayerId == playerId && value.Status == DbTicketStatus.Searching,
                        cancellationToken))
                {
                    throw ApiException.Conflict(
                        "ACTIVE_TICKET_EXISTS",
                        "The player already has an active matchmaking ticket.");
                }

                var rating = await db.PlayerRatings.SingleOrDefaultAsync(
                    value =>
                        value.PlayerId == playerId &&
                        value.RuleVersion == request.RuleVersion &&
                        value.TimeControl == timeControl,
                    cancellationToken);
                if (rating is null)
                {
                    rating = new PlayerRatingEntity
                    {
                        PlayerId = playerId,
                        RuleVersion = request.RuleVersion,
                        TimeControl = timeControl,
                        Rating = 1500,
                        GamesPlayed = 0,
                        Wins = 0,
                        Draws = 0,
                        Losses = 0,
                        UpdatedAt = now,
                        ConcurrencyStamp = 0
                    };
                    db.PlayerRatings.Add(rating);
                }

                created = new MatchmakingTicketEntity
                {
                    Id = Guid.NewGuid(),
                    PlayerId = playerId,
                    RuleVersion = request.RuleVersion,
                    TimeControl = timeControl,
                    MoveTimeLimitMilliseconds = GameOptionsCatalog.QuickMatchMoveTimeLimitMilliseconds,
                    RatingSnapshot = rating.Rating,
                    Status = DbTicketStatus.Searching,
                    CreatedAt = now,
                    LastHeartbeatAt = now,
                    ExpiresAt = now.Add(TicketLifetime),
                    ClientRequestId = requestId,
                    ConcurrencyStamp = 0
                };
                db.MatchmakingTickets.Add(created);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                foreach (var ticket in expired)
                {
                    metrics.RecordMatchmakingTicket(
                        "expired",
                        (now - ticket.CreatedAt).TotalMilliseconds);
                }
                metrics.RecordMatchmakingTicket("created", waitingMilliseconds: null);
                break;
            }
            catch (Exception exception) when (
                attempt < MaxTransactionAttempts &&
                MatchmakingConcurrency.IsRetryable(exception))
            {
                db.ChangeTracker.Clear();
            }
        }

        if (created is null)
        {
            throw new InvalidOperationException("The matchmaking ticket transaction did not complete.");
        }

        await coordinator.TryMatchAsync(cancellationToken);
        db.ChangeTracker.Clear();
        var current = await db.MatchmakingTickets.AsNoTracking()
            .SingleAsync(value => value.Id == created.Id, cancellationToken);
        var currentView = ToView(current);
        await lobbyNotifier.TicketUpdatedAsync(playerId, currentView, cancellationToken);
        return currentView;
    }

    public async Task<MatchTicketView> CurrentAsync(Guid playerId, CancellationToken cancellationToken)
    {
        var ticket = await db.MatchmakingTickets
            .Where(value => value.PlayerId == playerId)
            .OrderByDescending(value => value.CreatedAt)
            .ThenByDescending(value => value.Id)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw ApiException.NotFound();
        var now = timeProvider.GetUtcNow();
        if (ticket.Status == DbTicketStatus.Searching && ticket.ExpiresAt <= now)
        {
            ticket.Status = DbTicketStatus.Expired;
            ticket.ConcurrencyStamp++;
            await db.SaveChangesAsync(cancellationToken);
            metrics.RecordMatchmakingTicket(
                "expired",
                (now - ticket.CreatedAt).TotalMilliseconds);
            var expiredView = ToView(ticket);
            await lobbyNotifier.TicketUpdatedAsync(playerId, expiredView, cancellationToken);
            return expiredView;
        }

        return ToView(ticket);
    }

    public async Task<MatchTicketView> HeartbeatAsync(Guid playerId, Guid ticketId, CancellationToken cancellationToken)
    {
        var ticket = await db.MatchmakingTickets
            .SingleOrDefaultAsync(value => value.Id == ticketId && value.PlayerId == playerId, cancellationToken)
            ?? throw ApiException.NotFound();
        var now = timeProvider.GetUtcNow();
        if (ticket.Status == DbTicketStatus.Searching && ticket.ExpiresAt <= now)
        {
            ticket.Status = DbTicketStatus.Expired;
            ticket.ConcurrencyStamp++;
            await db.SaveChangesAsync(cancellationToken);
            metrics.RecordMatchmakingTicket(
                "expired",
                (now - ticket.CreatedAt).TotalMilliseconds);
            var expiredView = ToView(ticket);
            await lobbyNotifier.TicketUpdatedAsync(playerId, expiredView, cancellationToken);
            return expiredView;
        }

        if (ticket.Status != DbTicketStatus.Searching)
        {
            throw ApiException.Conflict("TICKET_NOT_SEARCHING", "Only a searching ticket can be renewed.", ticket.GameId);
        }

        ticket.LastHeartbeatAt = now;
        ticket.ExpiresAt = now.Add(TicketLifetime);
        ticket.ConcurrencyStamp++;
        await db.SaveChangesAsync(cancellationToken);
        var heartbeatView = ToView(ticket);
        await lobbyNotifier.TicketUpdatedAsync(playerId, heartbeatView, cancellationToken);
        return heartbeatView;
    }

    public async Task<MatchTicketView> CancelAsync(Guid playerId, Guid ticketId, CancellationToken cancellationToken)
    {
        MatchTicketView? cancelledView = null;
        for (var attempt = 1; attempt <= MaxTransactionAttempts; attempt++)
        {
            try
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(
                    IsolationLevel.ReadCommitted,
                    cancellationToken);
                var lockedTickets = await db.MatchmakingTickets
                    .FromSqlInterpolated(
                        $"SELECT * FROM matchmaking_tickets WHERE id = {ticketId} AND player_id = {playerId} FOR UPDATE")
                    .ToListAsync(cancellationToken);
                var ticket = lockedTickets.SingleOrDefault() ?? throw ApiException.NotFound();
                if (ticket.Status == DbTicketStatus.Matched)
                {
                    await transaction.CommitAsync(cancellationToken);
                    throw ApiException.Conflict(
                        "MATCH_ALREADY_CREATED",
                        "A game has already been created.",
                        ticket.GameId);
                }

                if (ticket.Status is DbTicketStatus.Cancelled or DbTicketStatus.Expired)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return ToView(ticket);
                }

                var now = timeProvider.GetUtcNow();
                ticket.Status = ticket.ExpiresAt <= now
                    ? DbTicketStatus.Expired
                    : DbTicketStatus.Cancelled;
                ticket.ConcurrencyStamp++;
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                metrics.RecordMatchmakingTicket(
                    ticket.Status == DbTicketStatus.Expired ? "expired" : "cancelled",
                    (now - ticket.CreatedAt).TotalMilliseconds);
                cancelledView = ToView(ticket);
                break;
            }
            catch (Exception exception) when (
                attempt < MaxTransactionAttempts &&
                MatchmakingConcurrency.IsRetryable(exception))
            {
                db.ChangeTracker.Clear();
            }
        }

        if (cancelledView is null)
        {
            throw new InvalidOperationException("The matchmaking cancellation transaction did not complete.");
        }

        await lobbyNotifier.TicketUpdatedAsync(playerId, cancelledView, cancellationToken);
        return cancelledView;
    }

    public static MatchTicketView ToView(MatchmakingTicketEntity ticket) => new(
        ticket.Id,
        ticket.RuleVersion,
        ticket.TimeControl,
        ticket.Status switch
        {
            DbTicketStatus.Searching => ApiTicketStatus.Searching,
            DbTicketStatus.Matched => ApiTicketStatus.Matched,
            DbTicketStatus.Cancelled => ApiTicketStatus.Cancelled,
            DbTicketStatus.Expired => ApiTicketStatus.Expired,
            _ => throw new ArgumentOutOfRangeException(nameof(ticket))
        },
        ticket.CreatedAt,
        ticket.LastHeartbeatAt,
        ticket.ExpiresAt,
        ticket.GameId,
        checked((int?)(ticket.MoveTimeLimitMilliseconds / 1000)));
}

internal static class MatchmakingConcurrency
{
    public static bool IsRetryable(Exception exception) =>
        (exception is DbUpdateConcurrencyException or
            PostgresException { SqlState: "40001" or "40P01" }) ||
        exception is DbUpdateException
        {
            InnerException: PostgresException
            {
                SqlState: "23505" or "40001" or "40P01"
            }
        };
}

public sealed record MatchSearchRange(
    int? EffectiveRadius,
    int? PopulationBaseRadius,
    int WaitingBonus,
    string PopulationBand,
    bool IsUnrestricted);

public static class MatchmakingPolicy
{
    public static MatchSearchRange Calculate(int eligiblePopulation, TimeSpan anchorWaitingTime)
    {
        var (populationRadius, populationBand) = eligiblePopulation switch
        {
            <= 1 => (0, "one"),
            <= 4 => ((int?)null, "2-4"),
            <= 9 => (400, "5-9"),
            <= 19 => (250, "10-19"),
            <= 49 => (150, "20-49"),
            _ => (100, "50+")
        };
        var totalSeconds = Math.Max(0, anchorWaitingTime.TotalSeconds);
        var waitingBonus = totalSeconds switch
        {
            < 15 => 0,
            < 30 => 100,
            < 45 => 200,
            < 60 => 400,
            _ => 0
        };
        var unrestricted = populationRadius is null || totalSeconds >= 60;
        return new MatchSearchRange(
            unrestricted ? null : populationRadius + waitingBonus,
            populationRadius,
            waitingBonus,
            populationBand,
            unrestricted);
    }
}

public sealed class MatchmakingCoordinator(
    IDbContextFactory<MistChessDbContext> contextFactory,
    GameFactory gameFactory,
    ILobbyNotifier lobbyNotifier,
    TimeProvider timeProvider,
    ILogger<MatchmakingCoordinator> logger,
    MistChessMetrics metrics)
{
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public async Task TryMatchAsync(CancellationToken cancellationToken)
    {
        const int maxTransactionAttempts = 3;

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var failedAttempts = 0;
            var deferredTicketIds = new HashSet<Guid>();
            while (true)
            {
                try
                {
                    if (!await TryCreateOneMatchAsync(deferredTicketIds, cancellationToken))
                    {
                        return;
                    }

                    failedAttempts = 0;
                }
                catch (Exception exception) when (MatchmakingConcurrency.IsRetryable(exception))
                {
                    failedAttempts++;
                    if (failedAttempts < maxTransactionAttempts)
                    {
                        continue;
                    }

                    logger.LogWarning(
                        exception,
                        "Matchmaking scan deferred after {AttemptCount} database concurrency conflicts",
                        failedAttempts);
                    return;
                }
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<bool> TryCreateOneMatchAsync(
        HashSet<Guid> deferredTicketIds,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var now = timeProvider.GetUtcNow();
        var excludedTicketIds = deferredTicketIds.ToArray();
        var expiredTickets = await db.MatchmakingTickets
            .FromSqlInterpolated(
                $"""
                SELECT *
                FROM matchmaking_tickets
                WHERE status = 'Searching'
                  AND expires_at <= {now}
                ORDER BY expires_at, id
                FOR UPDATE SKIP LOCKED
                LIMIT 100
                """)
            .ToListAsync(cancellationToken);
        foreach (var expired in expiredTickets)
        {
            expired.Status = DbTicketStatus.Expired;
            expired.ConcurrencyStamp++;
        }

        var anchors = await db.MatchmakingTickets
            .FromSqlInterpolated(
                $"""
                SELECT ticket.*
                FROM matchmaking_tickets AS ticket
                WHERE ticket.status = 'Searching'
                  AND ticket.id <> ALL ({excludedTicketIds})
                  AND ticket.time_control = {GameOptionsCatalog.QuickMatchTimeControlId}
                  AND ticket.move_time_limit_milliseconds = {GameOptionsCatalog.QuickMatchMoveTimeLimitMilliseconds}
                  AND ticket.expires_at > {now}
                  AND NOT EXISTS (
                      SELECT 1
                      FROM game_players AS participant
                      WHERE participant.player_id = ticket.player_id
                        AND participant.is_active)
                ORDER BY ticket.created_at, ticket.id
                FOR UPDATE SKIP LOCKED
                LIMIT 1
                """)
            .ToListAsync(cancellationToken);
        var anchor = anchors.SingleOrDefault();
        if (anchor is null)
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            RecordTicketOutcomes(expiredTickets, now);
            await NotifyTicketUpdatesAsync(expiredTickets, cancellationToken);
            return false;
        }

        var eligiblePopulation = await db.MatchmakingTickets
            .AsNoTracking()
            .Where(ticket =>
                ticket.Status == DbTicketStatus.Searching &&
                ticket.RuleVersion == anchor.RuleVersion &&
                ticket.TimeControl == GameOptionsCatalog.QuickMatchTimeControlId &&
                ticket.MoveTimeLimitMilliseconds == GameOptionsCatalog.QuickMatchMoveTimeLimitMilliseconds &&
                ticket.ExpiresAt > now &&
                !db.GamePlayers.Any(participant =>
                    participant.PlayerId == ticket.PlayerId &&
                    participant.IsActive))
            .Select(ticket => ticket.PlayerId)
            .Distinct()
            .CountAsync(cancellationToken);
        var searchRange = MatchmakingPolicy.Calculate(
            eligiblePopulation,
            now - anchor.CreatedAt);

        List<MatchmakingTicketEntity> candidates;
        if (searchRange.EffectiveRadius is { } radius)
        {
            candidates = await db.MatchmakingTickets
                .FromSqlInterpolated(
                    $"""
                    SELECT ticket.*
                    FROM matchmaking_tickets AS ticket
                    WHERE ticket.status = 'Searching'
                      AND ticket.id <> ALL ({excludedTicketIds})
                      AND ticket.id <> {anchor.Id}
                      AND ticket.player_id <> {anchor.PlayerId}
                      AND ticket.rule_version = {anchor.RuleVersion}
                      AND ticket.time_control = {GameOptionsCatalog.QuickMatchTimeControlId}
                      AND ticket.move_time_limit_milliseconds = {GameOptionsCatalog.QuickMatchMoveTimeLimitMilliseconds}
                      AND ticket.expires_at > {now}
                      AND ABS(ticket.rating_snapshot - {anchor.RatingSnapshot}) <= {radius}
                      AND NOT EXISTS (
                          SELECT 1
                          FROM game_players AS participant
                          WHERE participant.player_id = ticket.player_id
                            AND participant.is_active)
                    ORDER BY ABS(ticket.rating_snapshot - {anchor.RatingSnapshot}), ticket.created_at, ticket.id
                    FOR UPDATE SKIP LOCKED
                    LIMIT 1
                    """)
                .ToListAsync(cancellationToken);
        }
        else
        {
            candidates = await db.MatchmakingTickets
                .FromSqlInterpolated(
                    $"""
                    SELECT ticket.*
                    FROM matchmaking_tickets AS ticket
                    WHERE ticket.status = 'Searching'
                      AND ticket.id <> ALL ({excludedTicketIds})
                      AND ticket.id <> {anchor.Id}
                      AND ticket.player_id <> {anchor.PlayerId}
                      AND ticket.rule_version = {anchor.RuleVersion}
                      AND ticket.time_control = {GameOptionsCatalog.QuickMatchTimeControlId}
                      AND ticket.move_time_limit_milliseconds = {GameOptionsCatalog.QuickMatchMoveTimeLimitMilliseconds}
                      AND ticket.expires_at > {now}
                      AND NOT EXISTS (
                          SELECT 1
                          FROM game_players AS participant
                          WHERE participant.player_id = ticket.player_id
                            AND participant.is_active)
                    ORDER BY ABS(ticket.rating_snapshot - {anchor.RatingSnapshot}), ticket.created_at, ticket.id
                    FOR UPDATE SKIP LOCKED
                    LIMIT 1
                    """)
                .ToListAsync(cancellationToken);
        }

        var candidate = candidates.SingleOrDefault();
        var waitingMilliseconds = Math.Max(0, (now - anchor.CreatedAt).TotalMilliseconds);
        metrics.RecordMatchmakingScan(
            eligiblePopulation,
            searchRange,
            waitingMilliseconds,
            candidate is not null);
        logger.LogInformation(
            "Matchmaking scan anchorTicketId={AnchorTicketId} eligiblePopulation={EligiblePopulation} populationBand={PopulationBand} waitingMilliseconds={WaitingMilliseconds} populationBaseRadius={PopulationBaseRadius} waitingBonus={WaitingBonus} effectiveRadius={EffectiveRadius} unrestricted={Unrestricted}",
            anchor.Id,
            eligiblePopulation,
            searchRange.PopulationBand,
            (long)waitingMilliseconds,
            searchRange.PopulationBaseRadius,
            searchRange.WaitingBonus,
            searchRange.EffectiveRadius,
            searchRange.IsUnrestricted);
        if (candidate is null)
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            RecordTicketOutcomes(expiredTickets, now);
            await NotifyTicketUpdatesAsync(expiredTickets, cancellationToken);
            return false;
        }

        var pair = new[] { anchor, candidate };
        var playerIds = pair.Select(value => value.PlayerId).OrderBy(value => value).ToArray();
        var lockedPlayers = await db.GuestSessions
            .FromSqlInterpolated(
                $"""
                SELECT *
                FROM guest_sessions
                WHERE id IN ({playerIds[0]}, {playerIds[1]})
                ORDER BY id
                FOR UPDATE
                SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);
        if (lockedPlayers.Count != 2)
        {
            var lockedPlayerIds = lockedPlayers.Select(player => player.Id).ToHashSet();
            var busyTickets = pair
                .Where(ticket => !lockedPlayerIds.Contains(ticket.PlayerId))
                .ToArray();
            foreach (var ticket in busyTickets)
            {
                deferredTicketIds.Add(ticket.Id);
            }

            logger.LogDebug(
                "Matchmaking scan skipped busy player session locks ticketIds={TicketIds}",
                busyTickets.Select(ticket => ticket.Id));
            await transaction.RollbackAsync(cancellationToken);
            return true;
        }

        var bannedPlayerIds = lockedPlayers
            .Where(player => player.IsBanned)
            .Select(player => player.Id)
            .ToHashSet();
        if (bannedPlayerIds.Count > 0)
        {
            var bannedTickets = pair
                .Where(ticket =>
                    ticket.Status == DbTicketStatus.Searching &&
                    bannedPlayerIds.Contains(ticket.PlayerId))
                .ToArray();
            foreach (var ticket in bannedTickets)
            {
                ticket.Status = DbTicketStatus.Cancelled;
                ticket.ConcurrencyStamp++;
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            RecordTicketOutcomes(expiredTickets, now);
            RecordTicketOutcomes(bannedTickets, now);
            await NotifyTicketUpdatesAsync(
                expiredTickets.Concat(bannedTickets),
                cancellationToken);
            return true;
        }

        var invalidPair =
            pair.Any(ticket =>
                ticket.Status != DbTicketStatus.Searching ||
                ticket.ExpiresAt <= now ||
                ticket.RuleVersion != anchor.RuleVersion ||
                ticket.TimeControl != GameOptionsCatalog.QuickMatchTimeControlId ||
                ticket.MoveTimeLimitMilliseconds != GameOptionsCatalog.QuickMatchMoveTimeLimitMilliseconds) ||
            await db.GamePlayers.AnyAsync(
                value => playerIds.Contains(value.PlayerId) && value.IsActive,
                cancellationToken);
        if (invalidPair)
        {
            foreach (var ticket in pair.Where(value => value.Status == DbTicketStatus.Searching))
            {
                ticket.Status = ticket.ExpiresAt <= now
                    ? DbTicketStatus.Expired
                    : DbTicketStatus.Cancelled;
                ticket.ConcurrencyStamp++;
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            RecordTicketOutcomes(expiredTickets, now);
            RecordTicketOutcomes(pair, now);
            await NotifyTicketUpdatesAsync(expiredTickets.Concat(pair), cancellationToken);
            return true;
        }

        var game = gameFactory.Create(
            anchor.PlayerId,
            candidate.PlayerId,
            anchor.RuleVersion,
            GameOptionsCatalog.QuickMatchTimeControlId,
            GameOptionsCatalog.QuickMatchMoveTimeLimitMilliseconds,
            isRated: true);
        db.Games.Add(game);
        foreach (var ticket in pair)
        {
            ticket.Status = DbTicketStatus.Matched;
            ticket.GameId = game.Id;
            ticket.ConcurrencyStamp++;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        RecordTicketOutcomes(expiredTickets, now);
        RecordTicketOutcomes(pair, now);
        await NotifyTicketUpdatesAsync(expiredTickets, cancellationToken);
        foreach (var ticket in pair)
        {
            try
            {
                var ticketView = MatchmakingService.ToView(ticket);
                await lobbyNotifier.TicketUpdatedAsync(ticket.PlayerId, ticketView, cancellationToken);
                await lobbyNotifier.MatchFoundAsync(
                    ticket.PlayerId,
                    new MatchFoundView(ticket.Id, game.Id, GameFactory.GetSide(game, ticket.PlayerId)),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    exception,
                    "Match notification failed gameId={GameId} playerId={PlayerId}",
                    game.Id,
                    ticket.PlayerId);
            }
        }

        logger.LogInformation(
            "Match created gameId={GameId} redPlayerId={RedPlayerId} blackPlayerId={BlackPlayerId} ratingDifference={RatingDifference} elapsedMilliseconds={ElapsedMilliseconds}",
            game.Id,
            game.RedPlayerId,
            game.BlackPlayerId,
            Math.Abs(anchor.RatingSnapshot - candidate.RatingSnapshot),
            Math.Max(0, (long)(now - anchor.CreatedAt).TotalMilliseconds));
        metrics.RecordMatch(
            searchRange.PopulationBand,
            searchRange.IsUnrestricted,
            Math.Abs(anchor.RatingSnapshot - candidate.RatingSnapshot),
            waitingMilliseconds);
        return true;
    }

    private void RecordTicketOutcomes(
        IEnumerable<MatchmakingTicketEntity> tickets,
        DateTimeOffset now)
    {
        foreach (var ticket in tickets)
        {
            metrics.RecordMatchmakingTicket(
                ticket.Status.ToString().ToLowerInvariant(),
                (now - ticket.CreatedAt).TotalMilliseconds);
        }
    }

    private async Task NotifyTicketUpdatesAsync(
        IEnumerable<MatchmakingTicketEntity> tickets,
        CancellationToken cancellationToken)
    {
        foreach (var ticket in tickets)
        {
            try
            {
                await lobbyNotifier.TicketUpdatedAsync(
                    ticket.PlayerId,
                    MatchmakingService.ToView(ticket),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    exception,
                    "Ticket notification failed ticketId={TicketId} playerId={PlayerId}",
                    ticket.Id,
                    ticket.PlayerId);
            }
        }
    }

}

public sealed class MatchmakingWorker(
    MatchmakingCoordinator coordinator,
    ILogger<MatchmakingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await coordinator.TryMatchAsync(stoppingToken);
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The matchmaking scan failed.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
