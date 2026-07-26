using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
            game.TurnStartedAt = game.TimeControl is null ? null : now;
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

    public async Task<ReplayView> ReplayAsync(Guid gameId, Guid playerId, CancellationToken cancellationToken)
    {
        var game = await db.Games
            .AsNoTracking()
            .Include(value => value.Moves.OrderBy(move => move.Ply))
            .SingleOrDefaultAsync(
                value => value.Id == gameId && (value.RedPlayerId == playerId || value.BlackPlayerId == playerId),
                cancellationToken)
            ?? throw ApiException.NotFound();
        if (game.Status != GameStatus.Finished)
        {
            throw ApiException.NotFound();
        }

        var frames = new List<ReplayFrameView>(game.Moves.Count + 1);
        var initial = stateSerializer.Deserialize(game.InitialStateJson);
        frames.Add(new ReplayFrameView(0, initial.SideToMove, projector.ProjectFullPieces(initial), null));
        foreach (var move in game.Moves.OrderBy(value => value.Ply))
        {
            var state = stateSerializer.Deserialize(move.StateAfterJson);
            frames.Add(new ReplayFrameView(
                move.Ply,
                state.SideToMove,
                projector.ProjectFullPieces(state),
                new ReplayMoveView(
                    move.Ply,
                    move.Side,
                    move.MovingPieceType,
                    new ApiPosition(move.FromFile, move.FromRank),
                    new ApiPosition(move.ToFile, move.ToRank),
                    move.CapturedPieceType)));
        }

        return new ReplayView(
            game.Id,
            game.RuleVersion,
            GameFactory.GetSide(game, playerId),
            GameViewProjector.MapResult(game),
            frames);
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
        GameFactory.Finish(game, room, winner, reason, now);
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

    private static long ApplyElapsedClock(GameEntity game, DateTimeOffset now)
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

        return elapsed;
    }

    private static bool HasTimedOut(GameEntity game, Side side) => side == Side.Red
        ? game.RedMilliseconds == 0
        : game.BlackMilliseconds == 0;

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
    IGameNotifier notifier,
    TimeProvider timeProvider,
    ILogger<GameClockWorker> logger) : BackgroundService
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
        var now = timeProvider.GetUtcNow();
        var games = await db.Games
            .Include(value => value.Players)
            .Where(value => value.Status == GameStatus.Playing && value.TurnStartedAt != null)
            .ToListAsync(cancellationToken);
        foreach (var game in games)
        {
            var remaining = game.SideToMove == Side.Red ? game.RedMilliseconds : game.BlackMilliseconds;
            if (remaining is null || game.TurnStartedAt!.Value.AddMilliseconds(remaining.Value) > now)
            {
                continue;
            }

            var timedOutSide = game.SideToMove;
            if (timedOutSide == Side.Red)
            {
                game.RedMilliseconds = 0;
            }
            else
            {
                game.BlackMilliseconds = 0;
            }

            var room = await db.Rooms.SingleOrDefaultAsync(
                value => value.GameId == game.Id,
                cancellationToken);
            GameFactory.Finish(
                game,
                room,
                timedOutSide == Side.Red ? Side.Black : Side.Red,
                GameResultReason.Timeout.ToString(),
                now);
            await db.SaveChangesAsync(cancellationToken);
            await notifier.GameUpdatedAsync(game.Id, true, cancellationToken);
        }
    }
}
