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
    TimeProvider timeProvider)
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

        var timeControl = TimeControlSettings.Normalize(request.TimeControl);
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

                if (await db.GamePlayers.AnyAsync(
                        value => value.PlayerId == playerId && value.IsActive,
                        cancellationToken))
                {
                    throw ApiException.Conflict(
                        "ACTIVE_GAME_EXISTS",
                        "The player already has an unfinished game.");
                }

                if (await db.MatchmakingTickets.AnyAsync(
                        value => value.PlayerId == playerId && value.Status == DbTicketStatus.Searching,
                        cancellationToken))
                {
                    throw ApiException.Conflict(
                        "ACTIVE_TICKET_EXISTS",
                        "The player already has an active matchmaking ticket.");
                }

                created = new MatchmakingTicketEntity
                {
                    Id = Guid.NewGuid(),
                    PlayerId = playerId,
                    RuleVersion = request.RuleVersion,
                    TimeControl = timeControl,
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
        ticket.GameId);
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

public sealed class MatchmakingCoordinator(
    IDbContextFactory<MistChessDbContext> contextFactory,
    GameFactory gameFactory,
    ILobbyNotifier lobbyNotifier,
    TimeProvider timeProvider,
    ILogger<MatchmakingCoordinator> logger)
{
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public async Task TryMatchAsync(CancellationToken cancellationToken)
    {
        const int maxTransactionAttempts = 3;

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var failedAttempts = 0;
            while (true)
            {
                try
                {
                    if (!await TryCreateOneMatchAsync(cancellationToken))
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

    private async Task<bool> TryCreateOneMatchAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var now = timeProvider.GetUtcNow();
        var lockedSearchingTickets = await db.MatchmakingTickets
            .FromSqlInterpolated(
                $"SELECT * FROM matchmaking_tickets WHERE status = 'Searching' ORDER BY created_at, id FOR UPDATE")
            .ToListAsync(cancellationToken);
        var expiredTickets = lockedSearchingTickets
            .Where(value => value.ExpiresAt <= now)
            .ToArray();
        foreach (var expired in expiredTickets)
        {
            expired.Status = DbTicketStatus.Expired;
            expired.ConcurrencyStamp++;
        }

        var searching = lockedSearchingTickets
            .Where(value => value.ExpiresAt > now)
            .ToArray();
        var pair = searching
            .GroupBy(value => new { value.RuleVersion, value.TimeControl })
            .Select(group => group.Take(2).ToArray())
            .Where(values => values.Length == 2 && values[0].PlayerId != values[1].PlayerId)
            .OrderBy(values => values[0].CreatedAt)
            .ThenBy(values => values[0].Id)
            .FirstOrDefault();
        if (pair is null)
        {
            if (expiredTickets.Length > 0)
            {
                await db.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            await NotifyTicketUpdatesAsync(expiredTickets, cancellationToken);
            return false;
        }

        var playerIds = pair.Select(value => value.PlayerId).ToArray();
        if (await db.GamePlayers.AnyAsync(
                value => playerIds.Contains(value.PlayerId) && value.IsActive,
                cancellationToken))
        {
            foreach (var ticket in pair)
            {
                ticket.Status = DbTicketStatus.Cancelled;
                ticket.ConcurrencyStamp++;
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await NotifyTicketUpdatesAsync(expiredTickets.Concat(pair), cancellationToken);
            return true;
        }

        var game = gameFactory.Create(
            pair[0].PlayerId,
            pair[1].PlayerId,
            pair[0].RuleVersion,
            pair[0].TimeControl);
        db.Games.Add(game);
        foreach (var ticket in pair)
        {
            ticket.Status = DbTicketStatus.Matched;
            ticket.GameId = game.Id;
            ticket.ConcurrencyStamp++;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
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
            "Match created gameId={GameId} redPlayerId={RedPlayerId} blackPlayerId={BlackPlayerId}",
            game.Id,
            game.RedPlayerId,
            game.BlackPlayerId);
        return true;
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
