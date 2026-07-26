using System.Text.Json;
using System.Text.Json.Serialization;
using MistChess.Domain;

namespace MistChess.Infrastructure.Persistence;

public interface IGameStateSerializer
{
    string Serialize(GameState state);
    GameState Deserialize(string json);
}

public sealed class GameStateJsonSerializer : IGameStateSerializer
{
    public const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public string Serialize(GameState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var document = new StateDocument(
            CurrentFormatVersion,
            state.RuleVersion,
            state.SideToMove,
            state.HalfMoveCount,
            state.NoProgressHalfMoveCount,
            state.PositionHistory.ToArray(),
            state.Status,
            state.Result is null ? null : new ResultDocument(state.Result.Winner, state.Result.Reason),
            state.Pieces.Select(value => new PieceDocument(
                value.Position.File,
                value.Position.Rank,
                value.Piece.Side,
                value.Piece.Type)).ToArray(),
            state.RedLostPieces.ToArray(),
            state.BlackLostPieces.ToArray());

        return JsonSerializer.Serialize(document, Options);
    }

    public GameState Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var document = JsonSerializer.Deserialize<StateDocument>(json, Options)
            ?? throw new InvalidDataException("The persisted game state is empty.");

        if (document.FormatVersion != CurrentFormatVersion)
        {
            throw new NotSupportedException($"Game state format version {document.FormatVersion} is not supported.");
        }

        if (!StringComparer.Ordinal.Equals(document.RuleVersion, GameState.CurrentRuleVersion))
        {
            throw new NotSupportedException($"Rule version '{document.RuleVersion}' is not supported by this service.");
        }

        var placements = document.Pieces.Select(value => new PiecePlacement(
            new Position(value.File, value.Rank),
            new Piece(value.Side, value.Type)));
        var result = document.Result is null
            ? null
            : new GameResult(document.Result.Winner, document.Result.Reason);

        return GameState.Create(
            placements,
            document.SideToMove,
            document.HalfMoveCount,
            document.NoProgressHalfMoveCount,
            document.PositionHistory,
            document.Status,
            result,
            document.RuleVersion,
            document.RedLostPieces,
            document.BlackLostPieces);
    }

    private sealed record StateDocument(
        int FormatVersion,
        string RuleVersion,
        Side SideToMove,
        int HalfMoveCount,
        int NoProgressHalfMoveCount,
        string[] PositionHistory,
        GameStatus Status,
        ResultDocument? Result,
        PieceDocument[] Pieces,
        PieceType[] RedLostPieces,
        PieceType[] BlackLostPieces);

    private sealed record ResultDocument(Side? Winner, GameEndReason Reason);

    private sealed record PieceDocument(byte File, byte Rank, Side Side, PieceType Type);
}
