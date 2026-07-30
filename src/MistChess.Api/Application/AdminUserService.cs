using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using MistChess.Api.Contracts;
using MistChess.Domain;
using MistChess.Infrastructure.Persistence;
using DbTicketStatus = MistChess.Infrastructure.Persistence.MatchTicketStatus;

namespace MistChess.Api.Application;

public sealed class AdminUserService(
    MistChessDbContext db,
    HistoryService history,
    GameCompletionService completion,
    IGameNotifier gameNotifier,
    IAccountNotifier accountNotifier,
    TimeProvider timeProvider,
    IHostApplicationLifetime applicationLifetime,
    ILogger<AdminUserService> logger)
{
    private const int MaxTransactionAttempts = 3;

    public async Task<AdminUsersPageView> ListAsync(
        string? query,
        string? status,
        string? online,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 50)
        {
            throw ApiException.Unprocessable(
                "INVALID_ADMIN_USERS_LIMIT",
                "User list limit must be between 1 and 50.");
        }

        var normalizedStatus = NormalizeFilter(status);
        var normalizedOnline = NormalizeFilter(online);
        if (normalizedStatus is not ("all" or "active" or "banned"))
        {
            throw ApiException.Unprocessable(
                "INVALID_ADMIN_USER_STATUS",
                "User status must be all, active, or banned.");
        }

        if (normalizedOnline is not ("all" or "online" or "offline"))
        {
            throw ApiException.Unprocessable(
                "INVALID_ADMIN_USER_ONLINE_FILTER",
                "User online filter must be all, online, or offline.");
        }

        var observedAt = timeProvider.GetUtcNow();
        var onlineCutoff = observedAt.Subtract(GuestPresenceService.OnlineWindow);
        var users = db.GuestSessions.AsNoTracking().AsQueryable();

        var normalizedQuery = query?.Trim();
        if (!string.IsNullOrEmpty(normalizedQuery))
        {
            var pattern = $"%{EscapeLikePattern(normalizedQuery)}%";
            if (Guid.TryParse(normalizedQuery, out var playerId))
            {
                users = users.Where(player =>
                    player.Id == playerId ||
                    EF.Functions.ILike(player.DisplayName, pattern, "\\"));
            }
            else
            {
                users = users.Where(player =>
                    EF.Functions.ILike(player.DisplayName, pattern, "\\"));
            }
        }

        users = normalizedStatus switch
        {
            "active" => users.Where(player => !player.IsBanned),
            "banned" => users.Where(player => player.IsBanned),
            _ => users
        };
        users = normalizedOnline switch
        {
            "online" => users.Where(player =>
                !player.IsBanned &&
                player.ExpiresAt > observedAt &&
                player.LastSeenAt >= onlineCutoff),
            "offline" => users.Where(player =>
                player.IsBanned ||
                player.ExpiresAt <= observedAt ||
                player.LastSeenAt < onlineCutoff),
            _ => users
        };

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            var decoded = DecodeCursor(cursor);
            users = users.Where(player =>
                player.LastSeenAt < decoded.LastSeenAt ||
                (player.LastSeenAt == decoded.LastSeenAt && player.Id.CompareTo(decoded.PlayerId) < 0));
        }

        var primaryRatings = db.PlayerRatings.AsNoTracking().Where(rating =>
            rating.RuleVersion == GameState.CurrentRuleVersion &&
            rating.TimeControl == GameOptionsCatalog.QuickMatchTimeControlId);
        var pageQuery =
            from player in users
            join rating in primaryRatings on player.Id equals rating.PlayerId into ratingGroup
            from rating in ratingGroup.DefaultIfEmpty()
            orderby player.LastSeenAt descending, player.Id descending
            select new AdminUserRow(
                player.Id,
                player.DisplayName,
                player.CreatedAt,
                player.ExpiresAt,
                player.LastSeenAt,
                player.IsBanned,
                player.BannedAt,
                player.BanReason,
                player.BannedBy,
                rating == null ? RatingService.InitialRating : rating.Rating,
                rating == null ? 0 : rating.GamesPlayed,
                rating == null ? 0 : rating.Wins,
                rating == null ? 0 : rating.Draws,
                rating == null ? 0 : rating.Losses);

        var rows = await pageQuery.Take(limit + 1).ToListAsync(cancellationToken);
        var hasMore = rows.Count > limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var items = rows.Select(row => ToSummary(row, observedAt)).ToArray();
        var nextCursor = hasMore && rows.Count > 0
            ? EncodeCursor(rows[^1].LastSeenAt, rows[^1].PlayerId)
            : null;
        return new AdminUsersPageView(items, nextCursor, observedAt);
    }

    public async Task<AdminUserDetailView> DetailAsync(
        Guid playerId,
        CancellationToken cancellationToken)
    {
        var observedAt = timeProvider.GetUtcNow();
        var primaryRatings = db.PlayerRatings.AsNoTracking().Where(rating =>
            rating.RuleVersion == GameState.CurrentRuleVersion &&
            rating.TimeControl == GameOptionsCatalog.QuickMatchTimeControlId);
        var row = await (
                from player in db.GuestSessions.AsNoTracking()
                where player.Id == playerId
                join rating in primaryRatings on player.Id equals rating.PlayerId into ratingGroup
                from rating in ratingGroup.DefaultIfEmpty()
                select new AdminUserRow(
                    player.Id,
                    player.DisplayName,
                    player.CreatedAt,
                    player.ExpiresAt,
                    player.LastSeenAt,
                    player.IsBanned,
                    player.BannedAt,
                    player.BanReason,
                    player.BannedBy,
                    rating == null ? RatingService.InitialRating : rating.Rating,
                    rating == null ? 0 : rating.GamesPlayed,
                    rating == null ? 0 : rating.Wins,
                    rating == null ? 0 : rating.Draws,
                    rating == null ? 0 : rating.Losses))
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw ApiException.NotFound();
        var ratingRows = await db.PlayerRatings
            .AsNoTracking()
            .Where(rating => rating.PlayerId == playerId)
            .OrderByDescending(rating =>
                rating.RuleVersion == GameState.CurrentRuleVersion &&
                rating.TimeControl == GameOptionsCatalog.QuickMatchTimeControlId)
            .ThenBy(rating => rating.RuleVersion)
            .ThenBy(rating => rating.TimeControl)
            .ToListAsync(cancellationToken);
        var ratings = ratingRows.Select(ToRating).ToArray();
        return new AdminUserDetailView(ToSummary(row, observedAt), ratings, observedAt);
    }

    public async Task<AdminHistoricalGamesPageView> HistoryAsync(
        Guid playerId,
        string? cursor,
        int limit,
        string? ruleVersion,
        string? timeControl,
        string? result,
        CancellationToken cancellationToken)
    {
        if (!await db.GuestSessions.AsNoTracking().AnyAsync(
                player => player.Id == playerId,
                cancellationToken))
        {
            throw ApiException.NotFound();
        }

        var page = await history.ListAsync(
            playerId,
            cursor,
            limit,
            ruleVersion,
            timeControl,
            result,
            cancellationToken);
        var gameIds = page.Games.Select(game => game.GameId).ToArray();
        Dictionary<Guid, bool> ratedByGameId = gameIds.Length == 0
            ? []
            : await db.Games
                .AsNoTracking()
                .Where(game => gameIds.Contains(game.Id))
                .ToDictionaryAsync(game => game.Id, game => game.IsRated, cancellationToken);
        var games = page.Games.Select(game => new AdminHistoricalGameSummaryView(
            game.GameId,
            game.FinishedAt,
            game.RuleVersion,
            game.TimeControl,
            game.CurrentPlayerSide,
            game.Red,
            game.Black,
            game.Result,
            game.PlyCount,
            game.MoveTimeLimitSeconds,
            ratedByGameId[game.GameId])).ToArray();
        return new AdminHistoricalGamesPageView(games, page.NextCursor);
    }

    public async Task<AdminBanStatusView> BanAsync(
        Guid playerId,
        string reason,
        string adminName,
        CancellationToken cancellationToken)
    {
        var normalizedReason = reason?.Trim() ?? string.Empty;
        if (normalizedReason.Length is < 1 or > 200)
        {
            throw ApiException.Unprocessable(
                "INVALID_BAN_REASON",
                "A ban reason between 1 and 200 characters is required.");
        }

        AdminBanStatusView? result = null;
        Guid? forfeitedGameId = null;
        DateTimeOffset actionTime = default;
        var cancelledTicketCount = 0;
        var removedRoomCount = 0;
        var closedRoomCount = 0;
        for (var attempt = 1; attempt <= MaxTransactionAttempts; attempt++)
        {
            try
            {
                forfeitedGameId = null;
                actionTime = default;
                cancelledTicketCount = 0;
                removedRoomCount = 0;
                closedRoomCount = 0;
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(
                    IsolationLevel.ReadCommitted,
                    cancellationToken);
                var lockedPlayers = await db.GuestSessions
                    .FromSqlInterpolated(
                        $"SELECT * FROM guest_sessions WHERE id = {playerId} FOR UPDATE")
                    .ToListAsync(cancellationToken);
                var player = lockedPlayers.SingleOrDefault() ?? throw ApiException.NotFound();
                if (player.IsBanned)
                {
                    result = ToBanStatus(player);
                    await transaction.CommitAsync(cancellationToken);
                    logger.LogInformation(
                        "Admin user moderation returned existing state adminName={AdminName} targetPlayerId={TargetPlayerId} action={Action} actionTime={ActionTime} repeated={Repeated}",
                        adminName,
                        playerId,
                        "ban",
                        timeProvider.GetUtcNow(),
                        true);
                    return result;
                }

                actionTime = timeProvider.GetUtcNow();
                player.IsBanned = true;
                player.BannedAt = actionTime;
                player.BanReason = normalizedReason;
                player.BannedBy = adminName;

                var tickets = await db.MatchmakingTickets
                    .FromSqlInterpolated(
                        $"""
                        SELECT *
                        FROM matchmaking_tickets
                        WHERE player_id = {playerId}
                          AND status = 'Searching'
                        ORDER BY id
                        FOR UPDATE
                        """)
                    .ToListAsync(cancellationToken);
                foreach (var ticket in tickets)
                {
                    ticket.Status = DbTicketStatus.Cancelled;
                    ticket.ConcurrencyStamp++;
                }

                cancelledTicketCount = tickets.Count;
                var waitingRooms = await db.Rooms
                    .FromSqlInterpolated(
                        $"""
                        SELECT room.*
                        FROM rooms AS room
                        INNER JOIN room_players AS member ON member.room_id = room.id
                        WHERE member.player_id = {playerId}
                          AND room.status IN ('WaitingForOpponent', 'WaitingForReady')
                        ORDER BY room.id
                        FOR UPDATE OF room
                        """)
                    .ToListAsync(cancellationToken);
                foreach (var room in waitingRooms)
                {
                    await db.Entry(room).Collection(value => value.Players).LoadAsync(cancellationToken);
                    var member = room.Players.Single(value => value.PlayerId == playerId);
                    if (room.CreatorPlayerId == playerId)
                    {
                        db.Rooms.Remove(room);
                        closedRoomCount++;
                        continue;
                    }

                    db.RoomPlayers.Remove(member);
                    foreach (var remaining in room.Players.Where(value => value.PlayerId != playerId))
                    {
                        remaining.IsReady = false;
                    }

                    room.Status = GameStatus.WaitingForOpponent;
                    room.UpdatedAt = actionTime;
                    removedRoomCount++;
                }

                var activeGames = await db.Games
                    .FromSqlInterpolated(
                        $"""
                        SELECT game.*
                        FROM games AS game
                        INNER JOIN game_players AS participant ON participant.game_id = game.id
                        WHERE participant.player_id = {playerId}
                          AND participant.is_active
                        ORDER BY game.id
                        FOR UPDATE OF game
                        """)
                    .ToListAsync(cancellationToken);
                var game = activeGames.SingleOrDefault();
                if (game is not null)
                {
                    await db.Entry(game).Collection(value => value.Players).LoadAsync(cancellationToken);
                    if (game.Status == GameStatus.Playing)
                    {
                        var room = await db.Rooms.SingleOrDefaultAsync(
                            value => value.GameId == game.Id,
                            cancellationToken);
                        var winner = game.RedPlayerId == playerId ? Side.Black : Side.Red;
                        var completed = await completion.CompleteAsync(
                            db,
                            game,
                            room,
                            winner,
                            GameResultReason.AdministrativeForfeit.ToString(),
                            actionTime,
                            cancellationToken);
                        if (completed)
                        {
                            forfeitedGameId = game.Id;
                        }
                    }
                    else
                    {
                        foreach (var participant in game.Players)
                        {
                            participant.IsActive = false;
                        }
                    }
                }

                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                result = ToBanStatus(player);
                break;
            }
            catch (Exception exception) when (
                attempt < MaxTransactionAttempts &&
                MatchmakingConcurrency.IsRetryable(exception))
            {
                db.ChangeTracker.Clear();
            }
        }

        if (result is null)
        {
            throw new InvalidOperationException("The administrator ban transaction did not complete.");
        }

        logger.LogInformation(
            "Admin user moderation committed adminName={AdminName} targetPlayerId={TargetPlayerId} action={Action} actionTime={ActionTime} repeated={Repeated} cancelledTicketCount={CancelledTicketCount} removedRoomCount={RemovedRoomCount} closedRoomCount={ClosedRoomCount} forfeitedGameId={ForfeitedGameId}",
            adminName,
            playerId,
            "ban",
            actionTime,
            false,
            cancelledTicketCount,
            removedRoomCount,
            closedRoomCount,
            forfeitedGameId);
        if (forfeitedGameId is { } gameId)
        {
            await Task.WhenAll(
                gameNotifier.GameUpdatedAsync(
                    gameId,
                    ended: true,
                    applicationLifetime.ApplicationStopping),
                accountNotifier.AccountBannedAsync(
                    playerId,
                    gameId,
                    normalizedReason,
                    applicationLifetime.ApplicationStopping));
        }
        else
        {
            await accountNotifier.AccountBannedAsync(
                playerId,
                null,
                normalizedReason,
                applicationLifetime.ApplicationStopping);
        }

        return result;
    }

    public async Task<AdminBanStatusView> UnbanAsync(
        Guid playerId,
        string adminName,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxTransactionAttempts; attempt++)
        {
            try
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(
                    IsolationLevel.ReadCommitted,
                    cancellationToken);
                var lockedPlayers = await db.GuestSessions
                    .FromSqlInterpolated(
                        $"SELECT * FROM guest_sessions WHERE id = {playerId} FOR UPDATE")
                    .ToListAsync(cancellationToken);
                var player = lockedPlayers.SingleOrDefault() ?? throw ApiException.NotFound();
                var repeated = !player.IsBanned;
                if (!repeated)
                {
                    player.IsBanned = false;
                    player.BannedAt = null;
                    player.BanReason = null;
                    player.BannedBy = null;
                    await db.SaveChangesAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
                logger.LogInformation(
                    "Admin user moderation committed adminName={AdminName} targetPlayerId={TargetPlayerId} action={Action} actionTime={ActionTime} repeated={Repeated}",
                    adminName,
                    playerId,
                    "unban",
                    timeProvider.GetUtcNow(),
                    repeated);
                return ToBanStatus(player);
            }
            catch (Exception exception) when (
                attempt < MaxTransactionAttempts &&
                MatchmakingConcurrency.IsRetryable(exception))
            {
                db.ChangeTracker.Clear();
            }
        }

        throw new InvalidOperationException("The administrator unban transaction did not complete.");
    }

    private static string NormalizeFilter(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "all" : value.Trim().ToLowerInvariant();

    private static string EscapeLikePattern(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private static AdminUserSummaryView ToSummary(AdminUserRow row, DateTimeOffset observedAt)
    {
        var online = !row.Banned &&
            row.ExpiresAt > observedAt &&
            row.LastSeenAt >= observedAt.Subtract(GuestPresenceService.OnlineWindow);
        return new AdminUserSummaryView(
            row.PlayerId,
            row.DisplayName,
            row.CreatedAt,
            row.ExpiresAt,
            row.LastSeenAt,
            online,
            row.Banned,
            row.BannedAt,
            row.BanReason,
            row.BannedBy,
            row.Rating,
            row.GamesPlayed,
            row.Wins,
            row.Draws,
            row.Losses,
            WinRate(row.Wins, row.GamesPlayed));
    }

    private static AdminRatingView ToRating(PlayerRatingEntity rating) => new(
        rating.RuleVersion,
        rating.TimeControl,
        rating.Rating,
        rating.GamesPlayed,
        rating.Wins,
        rating.Draws,
        rating.Losses,
        WinRate(rating.Wins, rating.GamesPlayed),
        rating.UpdatedAt);

    private static decimal? WinRate(int wins, int gamesPlayed) =>
        gamesPlayed == 0
            ? null
            : Math.Round((decimal)wins / gamesPlayed * 100m, 1, MidpointRounding.AwayFromZero);

    private static AdminBanStatusView ToBanStatus(GuestSessionEntity player) => new(
        player.Id,
        player.IsBanned,
        player.BannedAt,
        player.BanReason,
        player.BannedBy);

    private static string EncodeCursor(DateTimeOffset lastSeenAt, Guid playerId)
    {
        var value = string.Create(
            CultureInfo.InvariantCulture,
            $"{lastSeenAt.UtcTicks}:{playerId:N}");
        return WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(value));
    }

    private static UserCursor DecodeCursor(string cursor)
    {
        try
        {
            var value = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(cursor));
            var separator = value.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0 ||
                !long.TryParse(
                    value.AsSpan(0, separator),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var ticks) ||
                !Guid.TryParseExact(value[(separator + 1)..], "N", out var playerId))
            {
                throw new FormatException();
            }

            return new UserCursor(new DateTimeOffset(ticks, TimeSpan.Zero), playerId);
        }
        catch (Exception exception) when (
            exception is FormatException or ArgumentException or OverflowException)
        {
            throw ApiException.Unprocessable(
                "INVALID_ADMIN_USERS_CURSOR",
                "The user list cursor is invalid.");
        }
    }

    private sealed record UserCursor(DateTimeOffset LastSeenAt, Guid PlayerId);

    private sealed record AdminUserRow(
        Guid PlayerId,
        string DisplayName,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt,
        DateTimeOffset LastSeenAt,
        bool Banned,
        DateTimeOffset? BannedAt,
        string? BanReason,
        string? BannedBy,
        int Rating,
        int GamesPlayed,
        int Wins,
        int Draws,
        int Losses);
}
