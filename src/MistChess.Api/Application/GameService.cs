using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.AspNetCore.WebUtilities;
using MistChess.Api.Contracts;
using MistChess.Domain;
using MistChess.Infrastructure.Persistence;
using Npgsql;
using ApiDrawStatus = MistChess.Api.Contracts.DrawOfferStatus;
using ApiGameView = MistChess.Api.Contracts.GameView;
using ApiPosition = MistChess.Api.Contracts.Position;
using DbDrawStatus = MistChess.Infrastructure.Persistence.DrawOfferStatus;
using DomainPosition = MistChess.Domain.Position;

namespace MistChess.Api.Application;

public sealed class GameService(
    MistChessDbContext db,
    IDbContextFactory<MistChessDbContext> contextFactory,
    IGameStateSerializer stateSerializer,
    GameViewProjector projector,
    GameCompletionService completion,
    IGameNotifier notifier,
    TimeProvider timeProvider,
    IHostApplicationLifetime applicationLifetime,
    ILogger<GameService> logger)
{
    public async Task<ApiGameView> GetAsync(Guid gameId, Guid playerId, CancellationToken cancellationToken)
    {
        var game = await db.Games
            .AsNoTracking()
            .SingleOrDefaultAsync(
                value => value.Id == gameId && (value.RedPlayerId == playerId || value.BlackPlayerId == playerId),
                cancellationToken)
            ?? throw ApiException.NotFound();
        var drawOffer = await db.DrawOffers.AsNoTracking().SingleOrDefaultAsync(
            value => value.GameId == game.Id && value.Status == DbDrawStatus.Pending,
            cancellationToken);
        return projector.Project(
            game,
            playerId,
            drawOffer is null ? null : GameViewProjector.MapDrawOffer(game, drawOffer));
    }

    public async Task<ApiGameView> MoveAsync(
        Guid gameId,
        Guid playerId,
        MoveRequest request,
        CancellationToken cancellationToken)
    {
        var clientMoveId = request.ClientMoveId.Trim();
        if (clientMoveId.Length is < 1 or > 64)
        {
            throw IllegalMove();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        GameEntity game;
        try
        {
            game = await LoadGameForCommandAsync(gameId, playerId, cancellationToken);
        }
        catch (Exception exception) when (IsMoveCommitConflict(exception))
        {
            return await ResolveMoveCommitConflictAsync(
                transaction,
                gameId,
                playerId,
                clientMoveId,
                exception);
        }

        var idempotent = await db.Moves
            .AsNoTracking()
            .SingleOrDefaultAsync(
                value => value.GameId == game.Id && value.ClientMoveId == clientMoveId,
                cancellationToken);
        if (idempotent is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            await notifier.GameUpdatedAsync(
                game.Id,
                game.Status == GameStatus.Finished,
                applicationLifetime.ApplicationStopping);
            return projector.ProjectHistoricalMove(game, idempotent, playerId);
        }
        var idempotentCommand = await db.MoveCommandReceipts
            .AsNoTracking()
            .SingleOrDefaultAsync(
                value => value.GameId == game.Id &&
                    value.PlayerId == playerId &&
                    value.ClientMoveId == clientMoveId,
                cancellationToken);
        if (idempotentCommand is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            await notifier.GameUpdatedAsync(
                game.Id,
                game.Status == GameStatus.Finished,
                applicationLifetime.ApplicationStopping);
            return projector.ProjectHistoricalCommand(game, idempotentCommand, playerId);
        }


        if (request.ExpectedVersion != game.Version)
        {
            throw ApiException.Conflict("STALE_VERSION", "The game version is stale.", game.Id);
        }

        if (game.Status != GameStatus.Playing)
        {
            throw IllegalMove();
        }

        var side = GameFactory.GetSide(game, playerId);
        if (side != game.SideToMove)
        {
            throw IllegalMove();
        }

        var now = ToDatabaseTimestamp(timeProvider.GetUtcNow());
        var (elapsed, timedOut) = await SettleElapsedClockAsync(game, now, cancellationToken);
        if (timedOut)
        {
            var receipt = CreateMoveCommandReceipt(game, playerId, clientMoveId, now);
            db.MoveCommandReceipts.Add(receipt);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (Exception exception) when (IsMoveCommitConflict(exception))
            {
                return await ResolveMoveCommitConflictAsync(
                    transaction,
                    gameId,
                    playerId,
                    clientMoveId,
                    exception);
            }

            await notifier.GameUpdatedAsync(game.Id, true, applicationLifetime.ApplicationStopping);
            return projector.ProjectHistoricalCommand(game, receipt, playerId);
        }

        MoveApplication application;
        try
        {
            var state = stateSerializer.Deserialize(game.StateJson);
            application = GameEngine.ApplyMove(
                state,
                new Move(ToDomain(request.From), ToDomain(request.To)));
        }
        catch (Exception exception) when (exception is IllegalMoveException or ArgumentOutOfRangeException or OverflowException)
        {
            throw IllegalMove();
        }

        var newState = application.State;
        game.StateJson = stateSerializer.Serialize(newState);
        game.SideToMove = newState.SideToMove;
        game.UpdatedAt = now;
        if (newState.Status == GameStatus.Finished && newState.Result is { } result)
        {
            await FinishGameAsync(
                game,
                result.Winner,
                GameViewProjector.PersistReason(result.Reason),
                now,
                cancellationToken);
        }
        else
        {
            AddIncrement(game, side);
            game.TurnMilliseconds = game.MoveTimeLimitMilliseconds;
            game.TurnStartedAt = game.TimeControl is null ? null : now;
            UpdateClockExpiry(game, now);
            game.Version++;
        }

        var withdrawnOffer = await WithdrawPendingDrawOfferAsync(game.Id, now, cancellationToken);
        var move = new MoveEntity
        {
            Id = Guid.NewGuid(),
            GameId = game.Id,
            Ply = newState.HalfMoveCount,
            FromFile = application.Event.Move.From.File,
            FromRank = application.Event.Move.From.Rank,
            ToFile = application.Event.Move.To.File,
            ToRank = application.Event.Move.To.Rank,
            Side = side,
            MovingPieceType = application.Event.MovingPiece.Type,
            CapturedPieceType = application.Event.CapturedPiece?.Type,
            ElapsedMilliseconds = elapsed,
            ClientMoveId = clientMoveId,
            PositionKey = newState.PositionKey,
            StateAfterJson = game.StateJson,
            GameVersion = game.Version,
            WinnerAfter = game.Winner,
            ResultReasonAfter = game.ResultReason,
            RedMillisecondsAfter = game.RedMilliseconds,
            BlackMillisecondsAfter = game.BlackMilliseconds,
            TurnStartedAtAfter = game.TurnStartedAt,
            TurnMillisecondsAfter = game.TurnMilliseconds,
            CreatedAt = now
        };
        db.Moves.Add(move);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception) when (IsMoveCommitConflict(exception))
        {
            return await ResolveMoveCommitConflictAsync(
                transaction,
                gameId,
                playerId,
                clientMoveId,
                exception);
        }

        if (withdrawnOffer is not null)
        {
            await notifier.DrawOfferChangedAsync(
                game.Id,
                ToDrawOfferView(game, withdrawnOffer),
                applicationLifetime.ApplicationStopping);
        }

        await notifier.GameUpdatedAsync(
            game.Id,
            game.Status == GameStatus.Finished,
            applicationLifetime.ApplicationStopping);
        logger.LogInformation(
            "Move committed gameId={GameId} playerId={PlayerId} version={Version} elapsedMilliseconds={ElapsedMilliseconds}",
            game.Id,
            playerId,
            game.Version,
            elapsed);
        return projector.ProjectHistoricalMove(game, move, playerId);
    }

    public async Task<ApiGameView> ResignAsync(Guid gameId, Guid playerId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var game = await LoadGameForCommandAsync(gameId, playerId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var (_, timedOut) = await SettleElapsedClockAsync(game, now, cancellationToken);
        if (timedOut)
        {
            await CommitTimedOutGameAsync(transaction, game, cancellationToken);
            return projector.Project(game, playerId);
        }

        if (game.Status == GameStatus.Finished)
        {
            await transaction.CommitAsync(cancellationToken);
            await notifier.GameUpdatedAsync(game.Id, true, applicationLifetime.ApplicationStopping);
            return projector.Project(game, playerId);
        }

        var side = GameFactory.GetSide(game, playerId);
        await FinishGameAsync(
            game,
            Opposite(side),
            GameResultReason.Resignation.ToString(),
            now,
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await notifier.GameUpdatedAsync(game.Id, true, applicationLifetime.ApplicationStopping);
        return projector.Project(game, playerId);
    }

    public async Task<DrawOfferView> OfferDrawAsync(Guid gameId, Guid playerId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var game = await LoadGameForCommandAsync(gameId, playerId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var (_, timedOut) = await SettleElapsedClockAsync(game, now, cancellationToken);
        if (timedOut)
        {
            await CommitTimedOutGameAsync(transaction, game, cancellationToken);
            throw ApiException.Conflict("GAME_FINISHED", "The game has already ended.", game.Id);
        }

        if (game.Status != GameStatus.Playing)
        {
            throw ApiException.Conflict("GAME_FINISHED", "The game has already ended.", game.Id);
        }

        var pending = await db.DrawOffers.SingleOrDefaultAsync(
            value => value.GameId == game.Id && value.Status == DbDrawStatus.Pending,
            cancellationToken);
        if (pending is not null)
        {
            if (pending.OfferedByPlayerId != playerId)
            {
                throw ApiException.Conflict("DRAW_OFFER_PENDING", "The opponent already has a pending draw offer.", game.Id);
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            var pendingView = ToDrawOfferView(game, pending);
            await notifier.DrawOfferChangedAsync(
                game.Id,
                pendingView,
                applicationLifetime.ApplicationStopping);
            return pendingView;
        }

        var offer = new DrawOfferEntity
        {
            Id = Guid.NewGuid(),
            GameId = game.Id,
            OfferedByPlayerId = playerId,
            Status = DbDrawStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.DrawOffers.Add(offer);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var view = ToDrawOfferView(game, offer);
        await notifier.DrawOfferChangedAsync(game.Id, view, applicationLifetime.ApplicationStopping);
        return view;
    }

    public async Task<ApiGameView> AcceptDrawAsync(Guid gameId, Guid playerId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var game = await LoadGameForCommandAsync(gameId, playerId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var (_, timedOut) = await SettleElapsedClockAsync(game, now, cancellationToken);
        if (timedOut)
        {
            await CommitTimedOutGameAsync(transaction, game, cancellationToken);
            return projector.Project(game, playerId);
        }

        var offer = await db.DrawOffers.SingleOrDefaultAsync(
            value => value.GameId == game.Id &&
                (value.Status == DbDrawStatus.Pending || value.Status == DbDrawStatus.Accepted),
            cancellationToken);
        if (game.Status == GameStatus.Finished)
        {
            if (offer is null ||
                offer.Status != DbDrawStatus.Accepted ||
                offer.OfferedByPlayerId == playerId ||
                !StringComparer.Ordinal.Equals(game.ResultReason, GameResultReason.AgreedDraw.ToString()))
            {
                throw ApiException.Conflict("GAME_FINISHED", "The game has already ended.", game.Id);
            }

            await transaction.CommitAsync(cancellationToken);
            await notifier.DrawOfferChangedAsync(
                game.Id,
                ToDrawOfferView(game, offer),
                applicationLifetime.ApplicationStopping);
            await notifier.GameUpdatedAsync(game.Id, true, applicationLifetime.ApplicationStopping);
            return projector.Project(game, playerId);
        }

        if (offer is null)
        {
            throw ApiException.Conflict("NO_DRAW_OFFER", "There is no pending draw offer.", game.Id);
        }
        if (offer.OfferedByPlayerId == playerId)
        {
            throw ApiException.Conflict("CANNOT_ACCEPT_OWN_OFFER", "The offering player cannot accept the draw.", game.Id);
        }

        offer.Status = DbDrawStatus.Accepted;
        offer.UpdatedAt = now;
        await FinishGameAsync(
            game,
            null,
            GameResultReason.AgreedDraw.ToString(),
            now,
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await notifier.DrawOfferChangedAsync(
            game.Id,
            ToDrawOfferView(game, offer),
            applicationLifetime.ApplicationStopping);
        await notifier.GameUpdatedAsync(game.Id, true, applicationLifetime.ApplicationStopping);
        return projector.Project(game, playerId);
    }

    public async Task<DrawOfferView> RejectDrawAsync(Guid gameId, Guid playerId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var game = await LoadGameForCommandAsync(gameId, playerId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var (_, timedOut) = await SettleElapsedClockAsync(game, now, cancellationToken);
        if (timedOut)
        {
            await CommitTimedOutGameAsync(transaction, game, cancellationToken);
            throw ApiException.Conflict("GAME_FINISHED", "The game has already ended.", game.Id);
        }

        if (game.Status != GameStatus.Playing)
        {
            throw ApiException.Conflict("GAME_FINISHED", "The game has already ended.", game.Id);
        }

        var offer = await db.DrawOffers.SingleOrDefaultAsync(
            value => value.GameId == game.Id && value.Status == DbDrawStatus.Pending,
            cancellationToken)
            ?? throw ApiException.Conflict("NO_DRAW_OFFER", "There is no pending draw offer.", game.Id);
        if (offer.OfferedByPlayerId == playerId)
        {
            throw ApiException.Conflict("CANNOT_REJECT_OWN_OFFER", "The offering player cannot reject the draw.", game.Id);
        }

        offer.Status = DbDrawStatus.Rejected;
        offer.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var view = ToDrawOfferView(game, offer);
        await notifier.DrawOfferChangedAsync(game.Id, view, applicationLifetime.ApplicationStopping);
        return view;
    }


    private async Task FinishGameAsync(
        GameEntity game,
        Side? winner,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var room = await db.Rooms.SingleOrDefaultAsync(
            value => value.GameId == game.Id,
            cancellationToken);
        await completion.CompleteAsync(
            db,
            game,
            room,
            winner,
            reason,
            now,
            cancellationToken);
    }

    private async Task<ApiGameView> ResolveMoveCommitConflictAsync(
        IDbContextTransaction transaction,
        Guid gameId,
        Guid playerId,
        string clientMoveId,
        Exception conflict)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (Exception rollbackException)
        {
            logger.LogWarning(
                rollbackException,
                "Move conflict rollback failed gameId={GameId} playerId={PlayerId}",
                gameId,
                playerId);
        }

        logger.LogInformation(
            conflict,
            "Move commit conflicted; reloading authoritative result gameId={GameId} playerId={PlayerId} clientMoveId={ClientMoveId}",
            gameId,
            playerId,
            clientMoveId);
        var recoveryToken = applicationLifetime.ApplicationStopping;
        await using var recoveryDb = await contextFactory.CreateDbContextAsync(recoveryToken);
        var historical = await recoveryDb.Moves
            .AsNoTracking()
            .SingleOrDefaultAsync(
                value => value.GameId == gameId && value.ClientMoveId == clientMoveId,
                recoveryToken);
        var historicalCommand = await recoveryDb.MoveCommandReceipts
            .AsNoTracking()
            .SingleOrDefaultAsync(
                value => value.GameId == gameId &&
                    value.PlayerId == playerId &&
                    value.ClientMoveId == clientMoveId,
                recoveryToken);
        var currentGame = await recoveryDb.Games
            .AsNoTracking()
            .SingleOrDefaultAsync(
                value => value.Id == gameId &&
                    (value.RedPlayerId == playerId || value.BlackPlayerId == playerId),
                recoveryToken)
            ?? throw ApiException.NotFound();
        if (historical is null && historicalCommand is null)
        {
            throw ApiException.Conflict("STALE_VERSION", "The game version is stale.", currentGame.Id);
        }

        await notifier.GameUpdatedAsync(
            currentGame.Id,
            currentGame.Status == GameStatus.Finished,
            recoveryToken);
        return historical is not null
            ? projector.ProjectHistoricalMove(currentGame, historical, playerId)
            : projector.ProjectHistoricalCommand(currentGame, historicalCommand!, playerId);
    }
    private static MoveCommandReceiptEntity CreateMoveCommandReceipt(
        GameEntity game,
        Guid playerId,
        string clientMoveId,
        DateTimeOffset createdAt) => new()
        {
            Id = Guid.NewGuid(),
            GameId = game.Id,
            PlayerId = playerId,
            ClientMoveId = clientMoveId,
            StateAfterJson = game.StateJson,
            GameVersion = game.Version,
            WinnerAfter = game.Winner,
            ResultReasonAfter = game.ResultReason,
            RedMillisecondsAfter = game.RedMilliseconds,
            BlackMillisecondsAfter = game.BlackMilliseconds,
            TurnStartedAtAfter = game.TurnStartedAt,
            TurnMillisecondsAfter = game.TurnMilliseconds,
            CreatedAt = createdAt
        };


    private static bool IsMoveCommitConflict(Exception exception) =>
        exception is DbUpdateConcurrencyException ||
        exception.GetBaseException() is PostgresException
        {
            SqlState: "23505" or "40001" or "40P01"
        };
    private async Task<GameEntity> LoadGameForCommandAsync(
        Guid gameId,
        Guid playerId,
        CancellationToken cancellationToken)
    {
        var lockedGames = await db.Games
            .FromSqlInterpolated(
                $"SELECT * FROM games WHERE id = {gameId} AND (red_player_id = {playerId} OR black_player_id = {playerId}) FOR UPDATE")
            .ToListAsync(cancellationToken);
        var game = lockedGames.SingleOrDefault() ?? throw ApiException.NotFound();
        await db.Entry(game).Collection(value => value.Players).LoadAsync(cancellationToken);
        return game;
    }

    private async Task<DrawOfferEntity?> WithdrawPendingDrawOfferAsync(
        Guid gameId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var offer = await db.DrawOffers.SingleOrDefaultAsync(
            value => value.GameId == gameId && value.Status == DbDrawStatus.Pending,
            cancellationToken);
        if (offer is null)
        {
            return null;
        }

        offer.Status = DbDrawStatus.Withdrawn;
        offer.UpdatedAt = now;
        return offer;
    }

    private static DrawOfferView ToDrawOfferView(GameEntity game, DrawOfferEntity offer) => new(
        offer.Status switch
        {
            DbDrawStatus.Pending => ApiDrawStatus.Pending,
            DbDrawStatus.Accepted => ApiDrawStatus.Accepted,
            DbDrawStatus.Rejected => ApiDrawStatus.Rejected,
            DbDrawStatus.Withdrawn => ApiDrawStatus.Withdrawn,
            _ => throw new ArgumentOutOfRangeException(nameof(offer))
        },
        GameFactory.GetSide(game, offer.OfferedByPlayerId));

    private async Task<(long Elapsed, bool TimedOut)> SettleElapsedClockAsync(
        GameEntity game,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (game.Status != GameStatus.Playing ||
            game.TimeControl is null ||
            game.TurnStartedAt is null)
        {
            return (0, false);
        }

        var elapsed = ApplyElapsedClock(game, now);
        var timedOutSide = game.SideToMove;
        if (!HasTimedOut(game, timedOutSide))
        {
            game.TurnStartedAt = now;
            UpdateClockExpiry(game, now);
            return (elapsed, false);
        }

        await FinishGameAsync(
            game,
            Opposite(timedOutSide),
            GameResultReason.Timeout.ToString(),
            now,
            cancellationToken);
        return (elapsed, true);
    }

    private async Task CommitTimedOutGameAsync(
        IDbContextTransaction transaction,
        GameEntity game,
        CancellationToken cancellationToken)
    {
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await notifier.GameUpdatedAsync(game.Id, true, applicationLifetime.ApplicationStopping);
    }

    internal static long ApplyElapsedClock(GameEntity game, DateTimeOffset now)
    {
        if (game.TimeControl is null || game.TurnStartedAt is not { } turnStartedAt)
        {
            return 0;
        }

        var elapsed = Math.Max(0, (long)(now - turnStartedAt).TotalMilliseconds);
        if (game.SideToMove == Side.Red)
        {
            game.RedMilliseconds = Math.Max(0, game.RedMilliseconds!.Value - elapsed);
        }
        else
        {
            game.BlackMilliseconds = Math.Max(0, game.BlackMilliseconds!.Value - elapsed);
        }

        if (game.TurnMilliseconds is { } turnMilliseconds)
        {
            game.TurnMilliseconds = Math.Max(0, turnMilliseconds - elapsed);
        }

        return elapsed;
    }

    internal static bool HasTimedOut(GameEntity game, Side side) =>
        game.TurnMilliseconds == 0 ||
        (side == Side.Red
            ? game.RedMilliseconds == 0
            : game.BlackMilliseconds == 0);

    private static void AddIncrement(GameEntity game, Side side)
    {
        var settings = TimeControlSettings.Parse(game.TimeControl);
        if (settings is null)
        {
            return;
        }

        if (side == Side.Red)
        {
            game.RedMilliseconds += settings.IncrementMilliseconds;
        }
        else
        {
            game.BlackMilliseconds += settings.IncrementMilliseconds;
        }
    }

    private static void UpdateClockExpiry(GameEntity game, DateTimeOffset now)
    {
        if (game.Status != GameStatus.Playing || game.TimeControl is null)
        {
            game.ClockExpiresAt = null;
            return;
        }

        var totalRemaining = game.SideToMove == Side.Red
            ? game.RedMilliseconds
            : game.BlackMilliseconds;
        long? expiryMilliseconds = totalRemaining is null
            ? null
            : Math.Min(totalRemaining.Value, game.TurnMilliseconds ?? long.MaxValue);
        game.ClockExpiresAt = expiryMilliseconds is null
            ? null
            : now.AddMilliseconds(expiryMilliseconds.Value);
    }

    private static DomainPosition ToDomain(ApiPosition position) => new(position.File, position.Rank);

    private static DateTimeOffset ToDatabaseTimestamp(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(
            utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMicrosecond),
            TimeSpan.Zero);
    }

    private static Side Opposite(Side side) => side == Side.Red ? Side.Black : Side.Red;

    private static ApiException IllegalMove() =>
        ApiException.Unprocessable("ILLEGAL_MOVE", "The requested move is illegal.");
}

public sealed class GameClockWorker(
    IDbContextFactory<MistChessDbContext> contextFactory,
    GameCompletionService completion,
    IGameNotifier notifier,
    TimeProvider timeProvider,
    ILogger<GameClockWorker> logger,
    MistChessMetrics metrics) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await FinishExpiredGamesAsync(stoppingToken);
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The game clock scan failed.");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    private async Task FinishExpiredGamesAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var now = timeProvider.GetUtcNow();
        var games = await db.Games
            .FromSqlInterpolated(
                $"""
                SELECT *
                FROM games
                WHERE status = 'Playing'
                  AND clock_expires_at IS NOT NULL
                  AND clock_expires_at <= {now}
                ORDER BY clock_expires_at, id
                FOR UPDATE SKIP LOCKED
                LIMIT 32
                """)
            .ToListAsync(cancellationToken);
        var expiredAtByGame = games.ToDictionary(
            game => game.Id,
            game => game.ClockExpiresAt!.Value);
        var completionByGame = new Dictionary<Guid, bool>(games.Count);

        foreach (var game in games)
        {
            await db.Entry(game).Collection(value => value.Players).LoadAsync(cancellationToken);
            var timedOutSide = game.SideToMove;
            GameService.ApplyElapsedClock(game, expiredAtByGame[game.Id]);
            if (!GameService.HasTimedOut(game, timedOutSide))
            {
                throw new InvalidDataException(
                    $"Game {game.Id} reached its persisted clock expiry without an exhausted clock.");
            }

            var room = await db.Rooms.SingleOrDefaultAsync(
                value => value.GameId == game.Id,
                cancellationToken);
            completionByGame[game.Id] = await completion.CompleteAsync(
                db,
                game,
                room,
                timedOutSide == Side.Red ? Side.Black : Side.Red,
                GameResultReason.Timeout.ToString(),
                now,
                cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        foreach (var game in games)
        {
            await notifier.GameUpdatedAsync(game.Id, true, cancellationToken);
            var delayMilliseconds = Math.Max(
                0,
                (now - expiredAtByGame[game.Id]).TotalMilliseconds);
            metrics.RecordClockTimeout(
                game.TimeControl,
                delayMilliseconds,
                duplicate: !completionByGame[game.Id]);
            logger.LogInformation(
                "Clock timeout completed gameId={GameId} timeControl={TimeControl} elapsedMilliseconds={ElapsedMilliseconds}",
                game.Id,
                game.TimeControl,
                (long)delayMilliseconds);
        }
    }
}

public sealed record HistoricalReplayResponse(HistoricalReplayView View, string ETag);

public sealed class HistoryService(
    MistChessDbContext db,
    IGameStateSerializer stateSerializer,
    GameViewProjector projector,
    TimeProvider timeProvider,
    ILogger<HistoryService> logger,
    MistChessMetrics metrics)
{
    public async Task<HistoricalGamesPageView> ListAsync(
        Guid playerId,
        string? cursor,
        int limit,
        string? ruleVersion,
        string? timeControl,
        string? result,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 50)
        {
            throw ApiException.Unprocessable("INVALID_HISTORY_LIMIT", "History limit must be between 1 and 50.");
        }

        var started = Stopwatch.GetTimestamp();
        var query = db.Games
            .AsNoTracking()
            .Where(game =>
                game.Status == GameStatus.Finished &&
                (game.RedPlayerId == playerId || game.BlackPlayerId == playerId));
        if (!string.IsNullOrWhiteSpace(ruleVersion))
        {
            var normalizedRuleVersion = ruleVersion.Trim();
            query = query.Where(game => game.RuleVersion == normalizedRuleVersion);
        }

        if (!string.IsNullOrWhiteSpace(timeControl))
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(timeControl.Trim(), "untimed"))
            {
                query = query.Where(game => game.TimeControl == null);
            }
            else
            {
                var normalizedTimeControl = TimeControlSettings.Normalize(timeControl);
                query = query.Where(game => game.TimeControl == normalizedTimeControl);
            }
        }

        if (!string.IsNullOrWhiteSpace(result))
        {
            query = result.Trim().ToLowerInvariant() switch
            {
                "win" => query.Where(game =>
                    (game.RedPlayerId == playerId && game.Winner == Side.Red) ||
                    (game.BlackPlayerId == playerId && game.Winner == Side.Black)),
                "loss" => query.Where(game =>
                    (game.RedPlayerId == playerId && game.Winner == Side.Black) ||
                    (game.BlackPlayerId == playerId && game.Winner == Side.Red)),
                "draw" => query.Where(game => game.Winner == null),
                _ => throw ApiException.Unprocessable(
                    "INVALID_HISTORY_RESULT",
                    "History result must be win, loss, or draw.")
            };
        }

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            var decoded = DecodeCursor(cursor);
            query = query.Where(game =>
                game.FinishedAt < decoded.FinishedAt ||
                (game.FinishedAt == decoded.FinishedAt && game.Id.CompareTo(decoded.GameId) < 0));
        }

        var rows = await query
            .OrderByDescending(game => game.FinishedAt)
            .ThenByDescending(game => game.Id)
            .Select(game => new HistoryRow(
                game.Id,
                game.FinishedAt!.Value,
                game.RuleVersion,
                game.TimeControl,
                game.MoveTimeLimitMilliseconds,
                game.RedPlayerId,
                game.BlackPlayerId,
                game.RedPlayer.DisplayName,
                game.BlackPlayer.DisplayName,
                game.Winner,
                game.ResultReason!,
                game.Moves.Count))
            .Take(limit + 1)
            .ToListAsync(cancellationToken);
        var hasMore = rows.Count > limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var games = rows.Select(row => ToSummary(row, playerId)).ToArray();
        var nextCursor = hasMore && rows.Count > 0
            ? EncodeCursor(rows[^1].FinishedAt, rows[^1].GameId)
            : null;
        var elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        logger.LogInformation(
            "History listed playerId={PlayerId} count={Count} elapsedMilliseconds={ElapsedMilliseconds}",
            playerId,
            games.Length,
            elapsedMilliseconds);
        metrics.RecordHistoryList(elapsedMilliseconds);
        return new HistoricalGamesPageView(games, nextCursor);
    }

    public async Task<HistoricalReplayResponse> PrivateReplayAsync(
        Guid gameId,
        Guid playerId,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var game = await ReplayQuery()
            .SingleOrDefaultAsync(
                value =>
                    value.Id == gameId &&
                    value.Status == GameStatus.Finished &&
                    (value.RedPlayerId == playerId || value.BlackPlayerId == playerId),
                cancellationToken)
            ?? throw ApiException.NotFound();
        var side = GameFactory.GetSide(game, playerId);
        var replay = BuildReplay(game, side);
        var elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        logger.LogInformation(
            "Private replay rebuilt gameId={GameId} frameCount={FrameCount} elapsedMilliseconds={ElapsedMilliseconds}",
            game.Id,
            replay.Frames.Count,
            elapsedMilliseconds);
        metrics.RecordReplayBuild(shared: false, replay.Frames.Count, elapsedMilliseconds);
        return new HistoricalReplayResponse(replay, CreateETag(game, side));
    }

    public async Task<HistoricalReplayView> SharedReplayAsync(
        string shareToken,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        string tokenHash;
        try
        {
            tokenHash = HashTokenOrNotFound(shareToken);
        }
        catch (ApiException)
        {
            metrics.RecordShareOperation("read_invalid");
            throw;
        }

        var game = await ReplayQuery()
            .SingleOrDefaultAsync(
                value =>
                    value.Status == GameStatus.Finished &&
                    value.ReplayShares.Any(share =>
                        share.TokenHash == tokenHash &&
                        share.RevokedAt == null),
                cancellationToken);
        if (game is null)
        {
            metrics.RecordShareOperation("read_invalid");
            throw ApiException.NotFound();
        }

        var replay = BuildReplay(game, null);
        var elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        logger.LogInformation(
            "Shared replay read gameId={GameId} frameCount={FrameCount} elapsedMilliseconds={ElapsedMilliseconds}",
            game.Id,
            replay.Frames.Count,
            elapsedMilliseconds);
        metrics.RecordReplayBuild(shared: true, replay.Frames.Count, elapsedMilliseconds);
        metrics.RecordShareOperation("read_valid");
        return replay;
    }

    public async Task<ReplayShareCreatedView> CreateShareAsync(
        Guid gameId,
        Guid playerId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var lockedGames = await db.Games
            .FromSqlInterpolated(
                $"""
                SELECT *
                FROM games
                WHERE id = {gameId}
                  AND status = 'Finished'
                  AND (red_player_id = {playerId} OR black_player_id = {playerId})
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);
        var game = lockedGames.SingleOrDefault() ?? throw ApiException.NotFound();
        var now = timeProvider.GetUtcNow();
        var existing = await db.ReplayShares.SingleOrDefaultAsync(
            value =>
                value.GameId == game.Id &&
                value.OwnerPlayerId == playerId &&
                value.RevokedAt == null,
            cancellationToken);
        if (existing is not null)
        {
            existing.RevokedAt = now;
        }

        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var share = new ReplayShareEntity
        {
            Id = Guid.NewGuid(),
            GameId = game.Id,
            OwnerPlayerId = playerId,
            TokenHash = HashToken(token),
            CreatedAt = now
        };
        db.ReplayShares.Add(share);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Replay share created gameId={GameId} ownerPlayerId={OwnerPlayerId} shareId={ShareId}",
            game.Id,
            playerId,
            share.Id);
        metrics.RecordShareOperation("created");
        return new ReplayShareCreatedView($"/shared/replay/{token}", now);
    }

    public async Task RevokeShareAsync(
        Guid gameId,
        Guid playerId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var lockedGames = await db.Games
            .FromSqlInterpolated(
                $"""
                SELECT *
                FROM games
                WHERE id = {gameId}
                  AND status = 'Finished'
                  AND (red_player_id = {playerId} OR black_player_id = {playerId})
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);
        var game = lockedGames.SingleOrDefault() ?? throw ApiException.NotFound();
        var share = await db.ReplayShares.SingleOrDefaultAsync(
            value =>
                value.GameId == game.Id &&
                value.OwnerPlayerId == playerId &&
                value.RevokedAt == null,
            cancellationToken);
        if (share is not null)
        {
            share.RevokedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Replay share revoked gameId={GameId} ownerPlayerId={OwnerPlayerId} existed={Existed}",
            game.Id,
            playerId,
            share is not null);
        metrics.RecordShareOperation(share is null ? "revoke_missing" : "revoked");
    }

    private IQueryable<GameEntity> ReplayQuery() => db.Games
        .AsNoTracking()
        .Include(value => value.RedPlayer)
        .Include(value => value.BlackPlayer)
        .Include(value => value.Moves.OrderBy(move => move.Ply));

    private HistoricalReplayView BuildReplay(GameEntity game, Side? currentPlayerSide)
    {
        var orderedMoves = game.Moves.OrderBy(value => value.Ply).ToArray();
        var frames = new List<HistoricalReplayFrameView>(orderedMoves.Length + 2);
        var initial = stateSerializer.Deserialize(game.InitialStateJson);
        var settings = TimeControlSettings.Parse(game.TimeControl);
        frames.Add(BuildFrame(
            initial,
            0,
            settings is null
                ? null
                : new ClockView(
                    settings.InitialMilliseconds,
                    settings.InitialMilliseconds,
                    game.CreatedAt,
                    game.MoveTimeLimitMilliseconds),
            null));

        foreach (var move in orderedMoves)
        {
            var state = stateSerializer.Deserialize(move.StateAfterJson);
            var replayMove = new ReplayMoveView(
                move.Ply,
                move.Side,
                move.MovingPieceType,
                new ApiPosition(move.FromFile, move.FromRank),
                new ApiPosition(move.ToFile, move.ToRank),
                move.CapturedPieceType);
            var clock = move.RedMillisecondsAfter is { } red &&
                move.BlackMillisecondsAfter is { } black
                ? new ClockView(red, black, move.CreatedAt, move.TurnMillisecondsAfter)
                : null;
            frames.Add(BuildFrame(state, move.Ply, clock, replayMove));
        }

        var lastMoveResult = orderedMoves.LastOrDefault()?.ResultReasonAfter;
        if (!StringComparer.Ordinal.Equals(lastMoveResult, game.ResultReason))
        {
            var finalState = stateSerializer.Deserialize(game.StateJson);
            var finalClock = game.RedMilliseconds is { } red &&
                game.BlackMilliseconds is { } black
                ? new ClockView(red, black, game.FinishedAt!.Value, game.TurnMilliseconds)
                : null;
            frames.Add(BuildFrame(finalState, finalState.HalfMoveCount, finalClock, null));
        }

        return new HistoricalReplayView(
            game.Id,
            game.RuleVersion,
            game.TimeControl,
            currentPlayerSide,
            ToHistoricalPlayer(game, Side.Red),
            ToHistoricalPlayer(game, Side.Black),
            GameViewProjector.MapResult(game),
            frames,
            ToSeconds(game.MoveTimeLimitMilliseconds));
    }

    private HistoricalReplayFrameView BuildFrame(
        GameState state,
        int ply,
        ClockView? clock,
        ReplayMoveView? move)
    {
        var redMove = move?.Side == Side.Red ? move : null;
        var blackMove = move?.Side == Side.Black ? move : null;
        return new HistoricalReplayFrameView(
            ply,
            state.SideToMove,
            clock,
            new ReplayFrameViewsView(
                projector.ProjectReplayFrame(state, Side.Red, redMove),
                projector.ProjectReplayFrame(state, Side.Black, blackMove),
                projector.ProjectReplayFrame(state, null, move)));
    }

    private static HistoricalGameSummaryView ToSummary(HistoryRow row, Guid playerId)
    {
        var currentSide = row.RedPlayerId == playerId ? Side.Red : Side.Black;
        return new HistoricalGameSummaryView(
            row.GameId,
            row.FinishedAt,
            row.RuleVersion,
            row.TimeControl,
            currentSide,
            new HistoricalPlayerView(
                row.RedDisplayName,
                OutcomeFor(Side.Red, row.Winner)),
            new HistoricalPlayerView(
                row.BlackDisplayName,
                OutcomeFor(Side.Black, row.Winner)),
            new GameResultView(
                row.Winner,
                Enum.Parse<GameResultReason>(row.ResultReason, ignoreCase: true)),
            row.PlyCount,
            ToSeconds(row.MoveTimeLimitMilliseconds));
    }

    private static HistoricalPlayerView ToHistoricalPlayer(GameEntity game, Side side)
    {
        var displayName = side == Side.Red
            ? game.RedPlayer.DisplayName
            : game.BlackPlayer.DisplayName;
        return new HistoricalPlayerView(displayName, OutcomeFor(side, game.Winner));
    }

    private static HistoricalOutcome OutcomeFor(Side side, Side? winner) => winner switch
    {
        null => HistoricalOutcome.Draw,
        _ when winner == side => HistoricalOutcome.Win,
        _ => HistoricalOutcome.Loss
    };

    private static string EncodeCursor(DateTimeOffset finishedAt, Guid gameId)
    {
        var value = string.Create(
            CultureInfo.InvariantCulture,
            $"{finishedAt.UtcTicks}:{gameId:N}");
        return WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(value));
    }

    private static HistoryCursor DecodeCursor(string cursor)
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
                !Guid.TryParseExact(value[(separator + 1)..], "N", out var gameId))
            {
                throw new FormatException();
            }

            return new HistoryCursor(new DateTimeOffset(ticks, TimeSpan.Zero), gameId);
        }
        catch (Exception exception) when (
            exception is FormatException or ArgumentException or OverflowException)
        {
            throw ApiException.Unprocessable("INVALID_HISTORY_CURSOR", "The history cursor is invalid.");
        }
    }

    private static string CreateETag(GameEntity game, Side? side)
    {
        var input = Encoding.UTF8.GetBytes(
            $"{game.Id:N}:{game.Version}:{game.Moves.Count}:{side?.ToString() ?? "shared"}");
        return $"\"{Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant()}\"";
    }

    private static string HashTokenOrNotFound(string token)
    {
        if (token.Length != 43)
        {
            throw ApiException.NotFound();
        }

        try
        {
            if (WebEncoders.Base64UrlDecode(token).Length != 32)
            {
                throw ApiException.NotFound();
            }
        }
        catch (FormatException)
        {
            throw ApiException.NotFound();
        }

        return HashToken(token);
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static int? ToSeconds(long? milliseconds) =>
        milliseconds is null ? null : checked((int)(milliseconds.Value / 1000));

    private sealed record HistoryCursor(DateTimeOffset FinishedAt, Guid GameId);

    private sealed record HistoryRow(
        Guid GameId,
        DateTimeOffset FinishedAt,
        string RuleVersion,
        string? TimeControl,
        long? MoveTimeLimitMilliseconds,
        Guid RedPlayerId,
        Guid BlackPlayerId,
        string RedDisplayName,
        string BlackDisplayName,
        Side? Winner,
        string ResultReason,
        int PlyCount);
}
