namespace MistChess.Domain;

public sealed record VisiblePiece(Side Side, PieceType Type, Position Position);

public sealed record CandidateMove(Position From, IReadOnlyList<Position> Destinations);

public sealed record CaptureSummary(
    IReadOnlyList<PieceType> RedLost,
    IReadOnlyList<PieceType> BlackLost);

public sealed record GameView(
    string RuleVersion,
    GameStatus Status,
    GameResult? Result,
    Side Perspective,
    Side SideToMove,
    IReadOnlyList<Position> VisibleSquares,
    IReadOnlyList<VisiblePiece> Pieces,
    IReadOnlyList<CandidateMove> CandidateMoves,
    CaptureSummary CaptureSummary);

public static class PlayerProjection
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

    public static IReadOnlySet<Position> ComputeVisibility(GameState state, Side side)
    {
        ArgumentNullException.ThrowIfNull(state);

        var visible = new HashSet<Position>();
        foreach (var placement in state.Pieces.Where(item => item.Piece.Side == side))
        {
            visible.Add(placement.Position);
            AddFixedVision(visible, placement.Position);
            AddRouteVision(visible, state, placement);
        }

        return visible;
    }

    public static GameView ProjectForPlayer(GameState state, Side side)
    {
        ArgumentNullException.ThrowIfNull(state);

        IReadOnlySet<Position> visibility = state.Status == GameStatus.Finished
            ? Enumerable.Range(0, GameState.BoardSize)
                .Select(index => new Position(index % GameState.BoardFiles, index / GameState.BoardFiles))
                .ToHashSet()
            : ComputeVisibility(state, side);
        var visibleSquares = visibility.OrderBy(position => position.Index).ToArray();
        var pieces = state.Pieces
            .Where(placement => placement.Piece.Side == side || visibility.Contains(placement.Position))
            .OrderBy(placement => placement.Position.Index)
            .Select(placement => new VisiblePiece(
                placement.Piece.Side,
                placement.Piece.Type,
                placement.Position))
            .ToArray();

        var candidates = state.Status == GameStatus.Playing && state.SideToMove == side
            ? state.Pieces
                .Where(placement => placement.Piece.Side == side)
                .OrderBy(placement => placement.Position.Index)
                .Select(placement => new CandidateMove(
                    placement.Position,
                    GameEngine.GenerateMoves(state, placement.Position)
                        .Select(move => move.To)
                        .OrderBy(position => position.Index)
                        .ToArray()))
                .Where(candidate => candidate.Destinations.Count > 0)
                .ToArray()
            : [];

        return new GameView(
            state.RuleVersion,
            state.Status,
            state.Result,
            side,
            state.SideToMove,
            visibleSquares,
            pieces,
            candidates,
            new CaptureSummary(state.RedLostPieces.ToArray(), state.BlackLostPieces.ToArray()));
    }

    private static void AddFixedVision(HashSet<Position> visible, Position from)
    {
        foreach (var direction in OrthogonalDirections)
        {
            if (GameEngine.Offset(from, direction.File, direction.Rank) is { } target)
            {
                visible.Add(target);
            }
        }
    }

    private static void AddRouteVision(
        HashSet<Position> visible,
        GameState state,
        PiecePlacement placement)
    {
        switch (placement.Piece.Type)
        {
            case PieceType.General:
                AddGeneralVision(visible, state, placement.Position, placement.Piece.Side);
                break;
            case PieceType.Advisor:
                AddAdvisorVision(visible, placement.Position, placement.Piece.Side);
                break;
            case PieceType.Elephant:
                AddElephantVision(visible, state, placement.Position, placement.Piece.Side);
                break;
            case PieceType.Horse:
                AddHorseVision(visible, state, placement.Position);
                break;
            case PieceType.Rook:
                AddRookVision(visible, state, placement.Position);
                break;
            case PieceType.Cannon:
                AddCannonVision(visible, state, placement.Position);
                break;
            case PieceType.Pawn:
                AddPawnVision(visible, placement.Position, placement.Piece.Side);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(placement.Piece.Type));
        }
    }

    private static void AddGeneralVision(
        HashSet<Position> visible,
        GameState state,
        Position from,
        Side side)
    {
        foreach (var direction in OrthogonalDirections)
        {
            if (GameEngine.Offset(from, direction.File, direction.Rank) is { } target &&
                GameEngine.IsInsidePalace(target, side))
            {
                visible.Add(target);
            }
        }

        foreach (var rankDelta in new[] { -1, 1 })
        {
            var route = new List<Position>();
            var cursor = GameEngine.Offset(from, 0, rankDelta);
            while (cursor is { } square)
            {
                route.Add(square);
                if (state.GetPiece(square) is { } encountered)
                {
                    if (encountered == new Piece(GameEngine.Opposite(side), PieceType.General))
                    {
                        visible.UnionWith(route);
                    }

                    break;
                }

                cursor = GameEngine.Offset(square, 0, rankDelta);
            }
        }
    }

    private static void AddAdvisorVision(HashSet<Position> visible, Position from, Side side)
    {
        foreach (var direction in DiagonalDirections)
        {
            if (GameEngine.Offset(from, direction.File, direction.Rank) is { } target &&
                GameEngine.IsInsidePalace(target, side))
            {
                visible.Add(target);
            }
        }
    }

    private static void AddElephantVision(
        HashSet<Position> visible,
        GameState state,
        Position from,
        Side side)
    {
        foreach (var direction in DiagonalDirections)
        {
            if (GameEngine.Offset(from, direction.File, direction.Rank) is not { } eye ||
                GameEngine.Offset(from, direction.File * 2, direction.Rank * 2) is not { } target ||
                !GameEngine.IsElephantSide(target, side))
            {
                continue;
            }

            visible.Add(eye);
            if (state.GetPiece(eye) is null)
            {
                visible.Add(target);
            }
        }
    }

    private static void AddHorseVision(HashSet<Position> visible, GameState state, Position from)
    {
        var groups = new[]
        {
            (LegFile: 0, LegRank: 1, Targets: new[] { (-1, 2), (1, 2) }),
            (LegFile: 1, LegRank: 0, Targets: new[] { (2, -1), (2, 1) }),
            (LegFile: 0, LegRank: -1, Targets: new[] { (-1, -2), (1, -2) }),
            (LegFile: -1, LegRank: 0, Targets: new[] { (-2, -1), (-2, 1) })
        };

        foreach (var group in groups)
        {
            if (GameEngine.Offset(from, group.LegFile, group.LegRank) is not { } leg)
            {
                continue;
            }

            visible.Add(leg);
            if (state.GetPiece(leg) is not null)
            {
                continue;
            }

            foreach (var targetOffset in group.Targets)
            {
                if (GameEngine.Offset(from, targetOffset.Item1, targetOffset.Item2) is { } target)
                {
                    visible.Add(target);
                }
            }
        }
    }

    private static void AddRookVision(HashSet<Position> visible, GameState state, Position from)
    {
        foreach (var direction in OrthogonalDirections)
        {
            var cursor = GameEngine.Offset(from, direction.File, direction.Rank);
            while (cursor is { } square)
            {
                visible.Add(square);
                if (state.GetPiece(square) is not null)
                {
                    break;
                }

                cursor = GameEngine.Offset(square, direction.File, direction.Rank);
            }
        }
    }

    private static void AddCannonVision(HashSet<Position> visible, GameState state, Position from)
    {
        foreach (var direction in OrthogonalDirections)
        {
            var occupiedSquares = 0;
            var cursor = GameEngine.Offset(from, direction.File, direction.Rank);
            while (cursor is { } square)
            {
                visible.Add(square);
                if (state.GetPiece(square) is not null && ++occupiedSquares == 2)
                {
                    break;
                }

                cursor = GameEngine.Offset(square, direction.File, direction.Rank);
            }
        }
    }

    private static void AddPawnVision(HashSet<Position> visible, Position from, Side side)
    {
        var forward = side == Side.Red ? 1 : -1;
        if (GameEngine.Offset(from, 0, forward) is { } forwardTarget)
        {
            visible.Add(forwardTarget);
        }

        var crossedRiver = side == Side.Red ? from.Rank >= 5 : from.Rank <= 4;
        if (!crossedRiver)
        {
            return;
        }

        foreach (var fileDelta in new[] { -1, 1 })
        {
            if (GameEngine.Offset(from, fileDelta, 0) is { } target)
            {
                visible.Add(target);
            }
        }
    }
}
