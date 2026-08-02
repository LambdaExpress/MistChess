using MistChess.Api.Contracts;
using MistChess.Domain;
using MistChess.Infrastructure.Persistence;
using ApiGameView = MistChess.Api.Contracts.GameView;
using ApiPosition = MistChess.Api.Contracts.Position;
using DomainGameResult = MistChess.Domain.GameResult;
using DomainGameView = MistChess.Domain.GameView;

namespace MistChess.Api.Application;

public sealed class GameViewProjector(IGameStateSerializer stateSerializer, TimeProvider timeProvider)
{
    public ApiGameView Project(
        GameEntity game,
        Guid playerId,
        DrawOfferView? drawOffer = null,
        TakebackRequestView? takebackRequest = null,
        MoveEntity? latestEffectiveMove = null)
    {
        var state = stateSerializer.Deserialize(game.StateJson);
        var perspective = GameFactory.GetSide(game, playerId);
        return ProjectCore(
            game.Id,
            game.RuleVersion,
            game.TimeControl,
            game.MoveTimeLimitMilliseconds,
            game.Version,
            game.Status,
            game.Winner,
            game.ResultReason,
            game.RedMilliseconds,
            game.BlackMilliseconds,
            game.TurnStartedAt,
            game.TurnMilliseconds,
            game.NegotiationVersion,
            state,
            perspective,
            timeProvider.GetUtcNow(),
            drawOffer,
            takebackRequest,
            MapLastAction(game),
            CanRequestTakeback(game, perspective, drawOffer, takebackRequest, latestEffectiveMove));
    }

    public ApiGameView ProjectHistoricalMove(
        GameEntity game,
        MoveEntity move,
        Guid playerId,
        DrawOfferView? drawOffer = null,
        TakebackRequestView? takebackRequest = null,
        MoveEntity? latestEffectiveMove = null)
    {
        var state = stateSerializer.Deserialize(move.StateAfterJson);
        var status = move.ResultReasonAfter is null ? state.Status : GameStatus.Finished;
        var perspective = GameFactory.GetSide(game, playerId);
        latestEffectiveMove ??= game.Version == move.GameVersion && move.RevertedAt is null ? move : null;
        return ProjectCore(
            game.Id,
            game.RuleVersion,
            game.TimeControl,
            game.MoveTimeLimitMilliseconds,
            move.GameVersion,
            status,
            move.WinnerAfter,
            move.ResultReasonAfter,
            move.RedMillisecondsAfter,
            move.BlackMillisecondsAfter,
            move.TurnStartedAtAfter,
            move.TurnMillisecondsAfter,
            game.NegotiationVersion,
            state,
            perspective,
            move.CreatedAt,
            drawOffer,
            takebackRequest,
            game.LastActionVersion == move.GameVersion ? MapLastAction(game) : null,
            game.Version == move.GameVersion &&
                CanRequestTakeback(game, perspective, drawOffer, takebackRequest, latestEffectiveMove));
    }
    public ApiGameView ProjectHistoricalCommand(
        GameEntity game,
        MoveCommandReceiptEntity receipt,
        Guid playerId,
        DrawOfferView? drawOffer = null,
        TakebackRequestView? takebackRequest = null,
        MoveEntity? latestEffectiveMove = null)
    {
        var state = stateSerializer.Deserialize(receipt.StateAfterJson);
        var status = receipt.ResultReasonAfter is null ? state.Status : GameStatus.Finished;
        var perspective = GameFactory.GetSide(game, playerId);
        return ProjectCore(
            game.Id,
            game.RuleVersion,
            game.TimeControl,
            game.MoveTimeLimitMilliseconds,
            receipt.GameVersion,
            status,
            receipt.WinnerAfter,
            receipt.ResultReasonAfter,
            receipt.RedMillisecondsAfter,
            receipt.BlackMillisecondsAfter,
            receipt.TurnStartedAtAfter,
            receipt.TurnMillisecondsAfter,
            game.NegotiationVersion,
            state,
            perspective,
            receipt.CreatedAt,
            drawOffer,
            takebackRequest,
            game.LastActionVersion == receipt.GameVersion ? MapLastAction(game) : null,
            game.Version == receipt.GameVersion &&
                CanRequestTakeback(game, perspective, drawOffer, takebackRequest, latestEffectiveMove));
    }


    public IReadOnlyList<PieceView> ProjectFullPieces(GameState state) => state.Pieces
        .OrderBy(value => (value.Position.Rank * GameState.BoardFiles) + value.Position.File)
        .Select(value => new PieceView(value.Piece.Side, value.Piece.Type, ToApi(value.Position)))
        .ToArray();

    public ReplayFrameProjectionView ProjectReplayFrame(
        GameState state,
        Side? perspective,
        ReplayMoveView? move)
    {
        var visibility = perspective is null
            ? Enumerable.Range(0, GameState.BoardSize)
                .Select(index => new MistChess.Domain.Position(
                    index % GameState.BoardFiles,
                    index / GameState.BoardFiles))
                .ToHashSet()
            : PlayerProjection.ComputeVisibility(state, perspective.Value);
        var pieces = state.Pieces
            .Where(value =>
                perspective is null ||
                value.Piece.Side == perspective.Value ||
                visibility.Contains(value.Position))
            .OrderBy(value => (value.Position.Rank * GameState.BoardFiles) + value.Position.File)
            .Select(value => new PieceView(
                value.Piece.Side,
                value.Piece.Type,
                ToApi(value.Position)))
            .ToArray();
        return new ReplayFrameProjectionView(
            visibility
                .OrderBy(value => (value.Rank * GameState.BoardFiles) + value.File)
                .Select(ToApi)
                .ToArray(),
            pieces,
            new CaptureSummaryView(
                state.RedLostPieces.ToArray(),
                state.BlackLostPieces.ToArray()),
            move);
    }

    public static GameResultView MapResult(GameEntity game)
    {
        if (game.Status != GameStatus.Finished || game.ResultReason is null)
        {
            throw new InvalidOperationException("A replay requires a persisted game result.");
        }

        return new GameResultView(game.Winner, ParseReason(game.ResultReason));
    }

    public static DrawOfferView MapDrawOffer(GameEntity game, DrawOfferEntity offer) => new(
        offer.Id,
        offer.Status switch
        {
            MistChess.Infrastructure.Persistence.DrawOfferStatus.Pending => MistChess.Api.Contracts.DrawOfferStatus.Pending,
            MistChess.Infrastructure.Persistence.DrawOfferStatus.Accepted => MistChess.Api.Contracts.DrawOfferStatus.Accepted,
            MistChess.Infrastructure.Persistence.DrawOfferStatus.Rejected => MistChess.Api.Contracts.DrawOfferStatus.Rejected,
            MistChess.Infrastructure.Persistence.DrawOfferStatus.Withdrawn => MistChess.Api.Contracts.DrawOfferStatus.Withdrawn,
            _ => throw new ArgumentOutOfRangeException(nameof(offer))
        },
        GameFactory.GetSide(game, offer.OfferedByPlayerId),
        game.NegotiationVersion);

    public static TakebackRequestView MapTakebackRequest(GameEntity game, TakebackRequestEntity request) => new(
        request.Id,
        request.Status switch
        {
            MistChess.Infrastructure.Persistence.TakebackRequestStatus.Pending => MistChess.Api.Contracts.TakebackRequestStatus.Pending,
            MistChess.Infrastructure.Persistence.TakebackRequestStatus.Accepted => MistChess.Api.Contracts.TakebackRequestStatus.Accepted,
            MistChess.Infrastructure.Persistence.TakebackRequestStatus.Rejected => MistChess.Api.Contracts.TakebackRequestStatus.Rejected,
            MistChess.Infrastructure.Persistence.TakebackRequestStatus.Withdrawn => MistChess.Api.Contracts.TakebackRequestStatus.Withdrawn,
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        },
        GameFactory.GetSide(game, request.RequestedByPlayerId),
        request.RequestedPly,
        request.RequestedAtVersion,
        request.ResolvedAtVersion,
        request.CreatedAt,
        game.NegotiationVersion);

    public static string PersistReason(GameEndReason reason) => reason switch
    {
        GameEndReason.GeneralCaptured => GameResultReason.GeneralCaptured.ToString(),
        GameEndReason.NoLegalMove => GameResultReason.NoLegalMove.ToString(),
        GameEndReason.Resignation => GameResultReason.Resignation.ToString(),
        GameEndReason.Timeout => GameResultReason.Timeout.ToString(),
        GameEndReason.AgreedDraw => GameResultReason.AgreedDraw.ToString(),
        GameEndReason.Repetition => GameResultReason.Repetition.ToString(),
        GameEndReason.NoProgress => GameResultReason.NoProgress.ToString(),
        GameEndReason.AdministrativeForfeit => GameResultReason.AdministrativeForfeit.ToString(),
        _ => throw new ArgumentOutOfRangeException(nameof(reason))
    };

    private static ApiGameView ProjectCore(
        Guid gameId,
        string ruleVersion,
        string? timeControl,
        long? moveTimeLimitMilliseconds,
        long version,
        GameStatus status,
        Side? winner,
        string? resultReason,
        long? redMilliseconds,
        long? blackMilliseconds,
        DateTimeOffset? turnStartedAt,
        long? turnMilliseconds,
        long negotiationVersion,
        GameState state,
        Side perspective,
        DateTimeOffset now,
        DrawOfferView? drawOffer,
        TakebackRequestView? takebackRequest,
        GameActionView? lastAction,
        bool canRequestTakeback)
    {
        if (!StringComparer.Ordinal.Equals(ruleVersion, state.RuleVersion))
        {
            throw new InvalidDataException("Persisted game metadata does not match its state rule version.");
        }

        var projectionState = PrepareProjectionState(state, status, winner, resultReason);
        var domainProjection = GameEngine.ProjectForPlayer(projectionState, perspective);
        var result = BuildResult(status, winner, resultReason);
        var clock = ComputeClock(
            redMilliseconds,
            blackMilliseconds,
            turnStartedAt,
            turnMilliseconds,
            state.SideToMove,
            status,
            now);
        return ToApiView(
            gameId,
            timeControl,
            ToSeconds(moveTimeLimitMilliseconds),
            version,
            status,
            result,
            domainProjection,
            clock,
            drawOffer,
            negotiationVersion,
            takebackRequest,
            lastAction,
            canRequestTakeback);
    }

    private static GameState PrepareProjectionState(
        GameState state,
        GameStatus status,
        Side? winner,
        string? resultReason)
    {
        if (status != GameStatus.Finished || state.Status == GameStatus.Finished)
        {
            return state;
        }

        if (!Enum.TryParse<GameEndReason>(resultReason, true, out var projectionReason))
        {
            throw new InvalidDataException($"Persisted game result reason '{resultReason}' is invalid.");
        }

        return state.Finish(new DomainGameResult(winner, projectionReason));
    }

    private static ApiGameView ToApiView(
        Guid gameId,
        string? timeControl,
        int? moveTimeLimitSeconds,
        long version,
        GameStatus status,
        GameResultView? result,
        DomainGameView projection,
        ClockView? clock,
        DrawOfferView? drawOffer,
        long negotiationVersion,
        TakebackRequestView? takebackRequest,
        GameActionView? lastAction,
        bool canRequestTakeback)
    {
        return new ApiGameView(
            gameId,
            projection.RuleVersion,
            timeControl,
            version,
            status,
            result,
            projection.Perspective,
            projection.SideToMove,
            projection.VisibleSquares.Select(ToApi).ToArray(),
            projection.Pieces.Select(value => new PieceView(value.Side, value.Type, ToApi(value.Position))).ToArray(),
            projection.CandidateMoves.Select(value => new CandidateMoveView(
                ToApi(value.From),
                value.Destinations.Select(ToApi).ToArray())).ToArray(),
            new CaptureSummaryView(
                projection.CaptureSummary.RedLost.ToArray(),
                projection.CaptureSummary.BlackLost.ToArray()),
            clock,
            status == GameStatus.Playing ? drawOffer : null,
            negotiationVersion,
            status == GameStatus.Playing ? takebackRequest : null,
            lastAction,
            status == GameStatus.Playing && canRequestTakeback,
            moveTimeLimitSeconds);
    }


    private static GameActionView? MapLastAction(GameEntity game)
    {
        if (game.LastActionVersion is not { } version ||
            game.LastActionKind is not { } kind ||
            game.LastActionActor is not { } actor)
        {
            return null;
        }

        var mappedKind = kind switch
        {
            "move" => GameActionKind.Move,
            "capture" => GameActionKind.Capture,
            "takebackAccepted" => GameActionKind.TakebackAccepted,
            _ => throw new InvalidDataException($"Persisted game action kind '{kind}' is invalid.")
        };
        return new GameActionView(version, mappedKind, actor);
    }

    private static bool CanRequestTakeback(
        GameEntity game,
        Side perspective,
        DrawOfferView? drawOffer,
        TakebackRequestView? takebackRequest,
        MoveEntity? latestEffectiveMove) =>
        game.Status == GameStatus.Playing &&
        !game.TakebackWindowConsumed &&
        drawOffer is null &&
        takebackRequest is null &&
        latestEffectiveMove is
        {
            RevertedAt: null
        } move &&
        move.GameVersion == game.Version &&
        move.Side == perspective &&
        game.SideToMove != perspective;

    private static ClockView? ComputeClock(
        long? redMilliseconds,
        long? blackMilliseconds,
        DateTimeOffset? turnStartedAt,
        long? turnMilliseconds,
        Side sideToMove,
        GameStatus status,
        DateTimeOffset now)
    {
        if (redMilliseconds is null || blackMilliseconds is null)
        {
            return null;
        }

        var red = redMilliseconds.Value;
        var black = blackMilliseconds.Value;
        var turn = turnMilliseconds;
        if (status == GameStatus.Playing && turnStartedAt is { } started)
        {
            var elapsed = Math.Max(0, (long)(now - started).TotalMilliseconds);
            if (sideToMove == Side.Red)
            {
                red = Math.Max(0, red - elapsed);
            }
            else
            {
                black = Math.Max(0, black - elapsed);
            }
            if (turn is not null)
            {
                turn = Math.Max(0, turn.Value - elapsed);
            }
        }

        return new ClockView(red, black, now, turn);
    }

    private static int? ToSeconds(long? milliseconds) =>
        milliseconds is null ? null : checked((int)(milliseconds.Value / 1000));

    private static GameResultView? BuildResult(GameStatus status, Side? winner, string? reason) =>
        status == GameStatus.Finished && reason is not null
            ? new GameResultView(winner, ParseReason(reason))
            : null;

    private static GameResultReason ParseReason(string reason) =>
        Enum.TryParse<GameResultReason>(reason, true, out var parsed)
            ? parsed
            : throw new InvalidDataException($"Persisted game result reason '{reason}' is invalid.");

    private static ApiPosition ToApi(MistChess.Domain.Position position) => new(position.File, position.Rank);
}
