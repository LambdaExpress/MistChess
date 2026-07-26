using System.Collections.ObjectModel;

namespace MistChess.Domain;

public sealed class GameState
{
    public const int BoardFiles = 9;
    public const int BoardRanks = 10;
    public const int BoardSize = BoardFiles * BoardRanks;
    public const string CurrentRuleVersion = "fog-xiangqi-v1";

    private readonly Piece?[] _board;
    private readonly ReadOnlyCollection<PiecePlacement> _pieces;
    private readonly ReadOnlyCollection<string> _positionHistory;
    private readonly ReadOnlyCollection<PieceType> _redLostPieces;
    private readonly ReadOnlyCollection<PieceType> _blackLostPieces;

    private GameState(
        Piece?[] board,
        Side sideToMove,
        int halfMoveCount,
        int noProgressHalfMoveCount,
        IEnumerable<string>? positionHistory,
        GameStatus status,
        GameResult? result,
        string ruleVersion,
        IEnumerable<PieceType>? redLostPieces,
        IEnumerable<PieceType>? blackLostPieces)
    {
        if (board.Length != BoardSize)
        {
            throw new ArgumentException($"A board must contain exactly {BoardSize} squares.", nameof(board));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(halfMoveCount);
        ArgumentOutOfRangeException.ThrowIfNegative(noProgressHalfMoveCount);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleVersion);

        if ((status == GameStatus.Finished) != (result is not null))
        {
            throw new ArgumentException("A finished state must have a result, and a non-finished state cannot have one.", nameof(result));
        }

        _board = (Piece?[])board.Clone();
        SideToMove = sideToMove;
        HalfMoveCount = halfMoveCount;
        NoProgressHalfMoveCount = noProgressHalfMoveCount;
        Status = status;
        Result = result;
        RuleVersion = ruleVersion;

        var pieces = new List<PiecePlacement>(32);
        for (var index = 0; index < _board.Length; index++)
        {
            if (_board[index] is { } piece)
            {
                pieces.Add(new PiecePlacement(FromIndex(index), piece));
            }
        }

        _pieces = pieces.AsReadOnly();
        _redLostPieces = Array.AsReadOnly(redLostPieces?.ToArray() ?? []);
        _blackLostPieces = Array.AsReadOnly(blackLostPieces?.ToArray() ?? []);

        PositionKey = GameEngine.ComputePositionKey(_board, sideToMove);
        var history = positionHistory?.ToList() ?? [];
        if (history.Count == 0 || !StringComparer.Ordinal.Equals(history[^1], PositionKey))
        {
            history.Add(PositionKey);
        }

        _positionHistory = history.AsReadOnly();
    }

    public Side SideToMove { get; }

    public int HalfMoveCount { get; }

    public int NoProgressHalfMoveCount { get; }

    public GameStatus Status { get; }

    public GameResult? Result { get; }

    public string RuleVersion { get; }

    public string PositionKey { get; }

    public IReadOnlyList<PiecePlacement> Pieces => _pieces;

    public IReadOnlyList<string> PositionHistory => _positionHistory;

    public IReadOnlyList<PieceType> RedLostPieces => _redLostPieces;

    public IReadOnlyList<PieceType> BlackLostPieces => _blackLostPieces;

    public Piece? GetPiece(Position position) => position.IsOnBoard ? _board[position.Index] : null;

    public static GameState CreateInitial() => Create(
        InitialPlacements(),
        Side.Red,
        ruleVersion: CurrentRuleVersion);

    public static GameState Create(
        IEnumerable<PiecePlacement> pieces,
        Side sideToMove = Side.Red,
        int halfMoveCount = 0,
        int noProgressHalfMoveCount = 0,
        IEnumerable<string>? positionHistory = null,
        GameStatus status = GameStatus.Playing,
        GameResult? result = null,
        string ruleVersion = CurrentRuleVersion,
        IEnumerable<PieceType>? redLostPieces = null,
        IEnumerable<PieceType>? blackLostPieces = null)
    {
        ArgumentNullException.ThrowIfNull(pieces);

        var board = new Piece?[BoardSize];
        foreach (var placement in pieces)
        {
            if (!placement.Position.IsOnBoard)
            {
                throw new ArgumentOutOfRangeException(nameof(pieces), "A piece position is outside the board.");
            }

            if (board[placement.Position.Index] is not null)
            {
                throw new ArgumentException("Two pieces cannot occupy the same square.", nameof(pieces));
            }

            board[placement.Position.Index] = placement.Piece;
        }

        return new GameState(
            board,
            sideToMove,
            halfMoveCount,
            noProgressHalfMoveCount,
            positionHistory,
            status,
            result,
            ruleVersion,
            redLostPieces,
            blackLostPieces);
    }

    public static GameState Create(Side sideToMove, params PiecePlacement[] pieces) =>
        Create(pieces, sideToMove);

    internal Piece?[] CopyBoard() => (Piece?[])_board.Clone();

    internal GameState AfterMove(
        Piece?[] board,
        Side sideToMove,
        int halfMoveCount,
        int noProgressHalfMoveCount,
        Piece? capturedPiece,
        GameResult? result)
    {
        var history = _positionHistory.ToList();
        var key = GameEngine.ComputePositionKey(board, sideToMove);
        history.Add(key);

        var redLost = _redLostPieces.ToList();
        var blackLost = _blackLostPieces.ToList();
        if (capturedPiece is { } captured)
        {
            (captured.Side == Side.Red ? redLost : blackLost).Add(captured.Type);
        }

        return new GameState(
            board,
            sideToMove,
            halfMoveCount,
            noProgressHalfMoveCount,
            history,
            result is null ? GameStatus.Playing : GameStatus.Finished,
            result,
            RuleVersion,
            redLost,
            blackLost);
    }

    public GameState Finish(GameResult result) => new(
        _board,
        SideToMove,
        HalfMoveCount,
        NoProgressHalfMoveCount,
        _positionHistory,
        GameStatus.Finished,
        result,
        RuleVersion,
        _redLostPieces,
        _blackLostPieces);

    private static Position FromIndex(int index) =>
        new(index % BoardFiles, index / BoardFiles);

    private static IEnumerable<PiecePlacement> InitialPlacements()
    {
        var backRank = new[]
        {
            PieceType.Rook,
            PieceType.Horse,
            PieceType.Elephant,
            PieceType.Advisor,
            PieceType.General,
            PieceType.Advisor,
            PieceType.Elephant,
            PieceType.Horse,
            PieceType.Rook
        };

        for (var file = 0; file < BoardFiles; file++)
        {
            yield return At(file, 0, Side.Red, backRank[file]);
            yield return At(file, 9, Side.Black, backRank[file]);
        }

        yield return At(1, 2, Side.Red, PieceType.Cannon);
        yield return At(7, 2, Side.Red, PieceType.Cannon);
        yield return At(1, 7, Side.Black, PieceType.Cannon);
        yield return At(7, 7, Side.Black, PieceType.Cannon);

        for (var file = 0; file < BoardFiles; file += 2)
        {
            yield return At(file, 3, Side.Red, PieceType.Pawn);
            yield return At(file, 6, Side.Black, PieceType.Pawn);
        }
    }

    private static PiecePlacement At(int file, int rank, Side side, PieceType type) =>
        new(new Position(file, rank), new Piece(side, type));
}
