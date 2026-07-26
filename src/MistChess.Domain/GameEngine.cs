using System.Text;

namespace MistChess.Domain;

public static class GameEngine
{
    private static readonly (int File, int Rank)[] OrthogonalDirections =
    [
        (0, 1),
        (1, 0),
        (0, -1),
        (-1, 0)
    ];

    private static readonly (int File, int Rank)[] DiagonalDirections =
    [
        (1, 1),
        (1, -1),
        (-1, -1),
        (-1, 1)
    ];

    private static readonly (int File, int Rank, int LegFile, int LegRank)[] HorseMoves =
    [
        (1, 2, 0, 1),
        (-1, 2, 0, 1),
        (2, 1, 1, 0),
        (2, -1, 1, 0),
        (1, -2, 0, -1),
        (-1, -2, 0, -1),
        (-2, -1, -1, 0),
        (-2, 1, -1, 0)
    ];

    public static IReadOnlyList<Move> GenerateMoves(GameState state, Position from)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!from.IsOnBoard || state.GetPiece(from) is not { } piece)
        {
            return [];
        }

        var destinations = piece.Type switch
        {
            PieceType.General => GeneralDestinations(state, from, piece.Side),
            PieceType.Advisor => AdvisorDestinations(state, from, piece.Side),
            PieceType.Elephant => ElephantDestinations(state, from, piece.Side),
            PieceType.Horse => HorseDestinations(state, from, piece.Side),
            PieceType.Rook => RookDestinations(state, from, piece.Side),
            PieceType.Cannon => CannonDestinations(state, from, piece.Side),
            PieceType.Pawn => PawnDestinations(state, from, piece.Side),
            _ => throw new ArgumentOutOfRangeException(nameof(piece.Type))
        };

        return destinations
            .Distinct()
            .OrderBy(position => position.Index)
            .Select(to => new Move(from, to))
            .ToArray();
    }

    public static bool IsGeneralThreatened(GameState state, Side side)
    {
        ArgumentNullException.ThrowIfNull(state);

        var generalPosition = state.Pieces
            .Where(placement => placement.Piece == new Piece(side, PieceType.General))
            .Select(placement => (Position?)placement.Position)
            .FirstOrDefault();
        if (generalPosition is null)
        {
            return false;
        }

        var enemy = Opposite(side);
        foreach (var placement in state.Pieces)
        {
            if (placement.Piece.Side == enemy &&
                GenerateMoves(state, placement.Position).Any(move => move.To == generalPosition.Value))
            {
                return true;
            }
        }

        return false;
    }

    public static bool HasAnyMove(GameState state, Side side)
    {
        ArgumentNullException.ThrowIfNull(state);

        return state.Pieces.Any(
            placement => placement.Piece.Side == side &&
                         GenerateMoves(state, placement.Position).Count > 0);
    }

    public static MoveApplication ApplyMove(GameState state, Move move)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.Status != GameStatus.Playing ||
            !move.From.IsOnBoard ||
            !move.To.IsOnBoard ||
            state.GetPiece(move.From) is not { } movingPiece ||
            movingPiece.Side != state.SideToMove ||
            !GenerateMoves(state, move.From).Contains(move))
        {
            throw new IllegalMoveException();
        }

        var board = state.CopyBoard();
        var capturedPiece = board[move.To.Index];
        board[move.From.Index] = null;
        board[move.To.Index] = movingPiece;

        var nextSide = Opposite(state.SideToMove);
        var halfMoveCount = checked(state.HalfMoveCount + 1);
        var noProgressCount = capturedPiece is not null || movingPiece.Type == PieceType.Pawn
            ? 0
            : checked(state.NoProgressHalfMoveCount + 1);

        GameResult? result = capturedPiece is { Type: PieceType.General }
            ? new GameResult(movingPiece.Side, GameEndReason.GeneralCaptured)
            : null;

        var nextState = state.AfterMove(
            board,
            nextSide,
            halfMoveCount,
            noProgressCount,
            capturedPiece,
            result);

        if (result is null)
        {
            result = EvaluateResult(nextState);
            if (result is not null)
            {
                nextState = nextState.Finish(result);
            }
        }

        return new MoveApplication(
            nextState,
            new MoveApplied(move, movingPiece, capturedPiece, result));
    }

    public static GameResult? EvaluateResult(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.Result is not null)
        {
            return state.Result;
        }

        if (!HasAnyMove(state, state.SideToMove))
        {
            return new GameResult(Opposite(state.SideToMove), GameEndReason.NoLegalMove);
        }

        if (state.PositionHistory.Count(key => StringComparer.Ordinal.Equals(key, state.PositionKey)) >= 3)
        {
            return new GameResult(null, GameEndReason.Repetition);
        }

        if (state.NoProgressHalfMoveCount >= 120)
        {
            return new GameResult(null, GameEndReason.NoProgress);
        }

        return null;
    }

    public static IReadOnlySet<Position> ComputeVisibility(GameState state, Side side) =>
        PlayerProjection.ComputeVisibility(state, side);

    public static GameView ProjectForPlayer(GameState state, Side side) =>
        PlayerProjection.ProjectForPlayer(state, side);

    public static string ComputePositionKey(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.PositionKey;
    }

    internal static string ComputePositionKey(Piece?[] board, Side sideToMove)
    {
        var builder = new StringBuilder(GameState.BoardSize + 2);
        foreach (var piece in board)
        {
            builder.Append(piece is { } value ? PieceKey(value) : '.');
        }

        builder.Append('|');
        builder.Append(sideToMove == Side.Red ? 'r' : 'b');
        return builder.ToString();
    }

    internal static Side Opposite(Side side) => side == Side.Red ? Side.Black : Side.Red;

    internal static bool IsInsidePalace(Position position, Side side) =>
        position.File is >= 3 and <= 5 &&
        (side == Side.Red ? position.Rank <= 2 : position.Rank >= 7);

    internal static bool IsElephantSide(Position position, Side side) =>
        side == Side.Red ? position.Rank <= 4 : position.Rank >= 5;

    internal static Position? Offset(Position position, int fileDelta, int rankDelta)
    {
        var file = position.File + fileDelta;
        var rank = position.Rank + rankDelta;
        return file is >= 0 and < GameState.BoardFiles && rank is >= 0 and < GameState.BoardRanks
            ? new Position(file, rank)
            : null;
    }

    private static IEnumerable<Position> GeneralDestinations(GameState state, Position from, Side side)
    {
        foreach (var direction in OrthogonalDirections)
        {
            if (Offset(from, direction.File, direction.Rank) is { } target &&
                IsInsidePalace(target, side) &&
                CanOccupy(state, target, side))
            {
                yield return target;
            }
        }

        foreach (var rankDelta in new[] { -1, 1 })
        {
            var cursor = Offset(from, 0, rankDelta);
            while (cursor is { } square)
            {
                if (state.GetPiece(square) is { } encountered)
                {
                    if (encountered == new Piece(Opposite(side), PieceType.General))
                    {
                        yield return square;
                    }

                    break;
                }

                cursor = Offset(square, 0, rankDelta);
            }
        }
    }

    private static IEnumerable<Position> AdvisorDestinations(GameState state, Position from, Side side)
    {
        foreach (var direction in DiagonalDirections)
        {
            if (Offset(from, direction.File, direction.Rank) is { } target &&
                IsInsidePalace(target, side) &&
                CanOccupy(state, target, side))
            {
                yield return target;
            }
        }
    }

    private static IEnumerable<Position> ElephantDestinations(GameState state, Position from, Side side)
    {
        foreach (var direction in DiagonalDirections)
        {
            if (Offset(from, direction.File, direction.Rank) is not { } eye ||
                Offset(from, direction.File * 2, direction.Rank * 2) is not { } target ||
                !IsElephantSide(target, side) ||
                state.GetPiece(eye) is not null ||
                !CanOccupy(state, target, side))
            {
                continue;
            }

            yield return target;
        }
    }

    private static IEnumerable<Position> HorseDestinations(GameState state, Position from, Side side)
    {
        foreach (var candidate in HorseMoves)
        {
            if (Offset(from, candidate.LegFile, candidate.LegRank) is not { } leg ||
                state.GetPiece(leg) is not null ||
                Offset(from, candidate.File, candidate.Rank) is not { } target ||
                !CanOccupy(state, target, side))
            {
                continue;
            }

            yield return target;
        }
    }

    private static IEnumerable<Position> RookDestinations(GameState state, Position from, Side side)
    {
        foreach (var direction in OrthogonalDirections)
        {
            var cursor = Offset(from, direction.File, direction.Rank);
            while (cursor is { } square)
            {
                var encountered = state.GetPiece(square);
                if (encountered is null)
                {
                    yield return square;
                }
                else
                {
                    if (encountered.Value.Side != side)
                    {
                        yield return square;
                    }

                    break;
                }

                cursor = Offset(square, direction.File, direction.Rank);
            }
        }
    }

    private static IEnumerable<Position> CannonDestinations(GameState state, Position from, Side side)
    {
        foreach (var direction in OrthogonalDirections)
        {
            var screenFound = false;
            var cursor = Offset(from, direction.File, direction.Rank);
            while (cursor is { } square)
            {
                var encountered = state.GetPiece(square);
                if (!screenFound)
                {
                    if (encountered is null)
                    {
                        yield return square;
                    }
                    else
                    {
                        screenFound = true;
                    }
                }
                else if (encountered is not null)
                {
                    if (encountered.Value.Side != side)
                    {
                        yield return square;
                    }

                    break;
                }

                cursor = Offset(square, direction.File, direction.Rank);
            }
        }
    }

    private static IEnumerable<Position> PawnDestinations(GameState state, Position from, Side side)
    {
        var forward = side == Side.Red ? 1 : -1;
        if (Offset(from, 0, forward) is { } forwardTarget && CanOccupy(state, forwardTarget, side))
        {
            yield return forwardTarget;
        }

        var crossedRiver = side == Side.Red ? from.Rank >= 5 : from.Rank <= 4;
        if (!crossedRiver)
        {
            yield break;
        }

        foreach (var fileDelta in new[] { -1, 1 })
        {
            if (Offset(from, fileDelta, 0) is { } target && CanOccupy(state, target, side))
            {
                yield return target;
            }
        }
    }

    private static bool CanOccupy(GameState state, Position target, Side side) =>
        state.GetPiece(target) is not { } occupant || occupant.Side != side;

    private static char PieceKey(Piece piece)
    {
        var key = piece.Type switch
        {
            PieceType.General => 'g',
            PieceType.Advisor => 'a',
            PieceType.Elephant => 'e',
            PieceType.Horse => 'h',
            PieceType.Rook => 'r',
            PieceType.Cannon => 'c',
            PieceType.Pawn => 'p',
            _ => throw new ArgumentOutOfRangeException(nameof(piece.Type))
        };

        return piece.Side == Side.Red ? char.ToUpperInvariant(key) : key;
    }
}
