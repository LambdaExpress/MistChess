using FluentAssertions;
using MistChess.Domain;
using MistChess.Infrastructure.Persistence;

namespace MistChess.Api.Tests;

[Trait("Category", "Security")]
public sealed class GameStatePersistenceTests
{
    private readonly GameStateJsonSerializer _serializer = new();

    [Fact]
    public void Versioned_state_round_trips_every_rule_relevant_field()
    {
        var initial = GameState.CreateInitial();
        var application = GameEngine.ApplyMove(
            initial,
            GameEngine.GenerateMoves(initial, new Position(0, 0))[0]);

        var json = _serializer.Serialize(application.State);
        var restored = _serializer.Deserialize(json);

        json.Should().Contain("\"formatVersion\":1");
        restored.RuleVersion.Should().Be(application.State.RuleVersion);
        restored.SideToMove.Should().Be(application.State.SideToMove);
        restored.HalfMoveCount.Should().Be(application.State.HalfMoveCount);
        restored.NoProgressHalfMoveCount.Should().Be(application.State.NoProgressHalfMoveCount);
        restored.PositionHistory.Should().Equal(application.State.PositionHistory);
        restored.RedLostPieces.Should().Equal(application.State.RedLostPieces);
        restored.BlackLostPieces.Should().Equal(application.State.BlackLostPieces);
        restored.Pieces.Should().BeEquivalentTo(application.State.Pieces);
    }

    [Fact]
    public void Unknown_state_format_fails_instead_of_silently_reinterpreting_data()
    {
        var json = _serializer.Serialize(GameState.CreateInitial())
            .Replace("\"formatVersion\":1", "\"formatVersion\":2", StringComparison.Ordinal);

        var act = () => _serializer.Deserialize(json);

        act.Should().Throw<NotSupportedException>();
    }
}
