namespace MistChess.Domain;

public enum Side
{
    Red,
    Black
}

public enum PieceType
{
    General,
    Advisor,
    Elephant,
    Horse,
    Rook,
    Cannon,
    Pawn
}

public enum GameStatus
{
    WaitingForOpponent,
    WaitingForReady,
    Playing,
    Finished
}

public enum GameEndReason
{
    GeneralCaptured,
    NoLegalMove,
    Resignation,
    Timeout,
    AgreedDraw,
    Repetition,
    NoProgress
}

public readonly record struct Position(byte File, byte Rank)
{
    public Position(int file, int rank)
        : this(checked((byte)file), checked((byte)rank))
    {
    }

    internal bool IsOnBoard => File < GameState.BoardFiles && Rank < GameState.BoardRanks;

    internal int Index => IsOnBoard
        ? (Rank * GameState.BoardFiles) + File
        : throw new ArgumentOutOfRangeException(nameof(Position), "The position is outside the board.");
}

public readonly record struct Move(Position From, Position To);

public readonly record struct Piece(Side Side, PieceType Type);

public readonly record struct PiecePlacement(Position Position, Piece Piece);

public sealed record GameResult(Side? Winner, GameEndReason Reason)
{
    public bool IsDraw => Winner is null;
}

public sealed record MoveApplied(
    Move Move,
    Piece MovingPiece,
    Piece? CapturedPiece,
    GameResult? Result);

public sealed record MoveApplication(GameState State, MoveApplied Event);

public sealed class IllegalMoveException : InvalidOperationException
{
    public IllegalMoveException()
        : base("The requested move is illegal.")
    {
    }
}
