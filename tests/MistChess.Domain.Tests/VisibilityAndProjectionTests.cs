using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;

namespace MistChess.Domain.Tests;

public sealed class VisibilityAndProjectionTests
{
    public static TheoryData<PieceType, Position, Position[]> CompleteVisibilityCases => new()
    {
        {
            PieceType.General,
            Pos(4, 1),
            Positions((4, 1), (4, 2), (5, 1), (4, 0), (3, 1))
        },
        {
            PieceType.Advisor,
            Pos(4, 1),
            Positions((4, 1), (4, 2), (5, 1), (4, 0), (3, 1),
                (3, 0), (5, 0), (3, 2), (5, 2))
        },
        {
            PieceType.Elephant,
            Pos(4, 2),
            Positions((4, 2), (4, 3), (5, 2), (4, 1), (3, 2),
                (5, 3), (6, 4), (3, 3), (2, 4), (5, 1), (6, 0), (3, 1), (2, 0))
        },
        {
            PieceType.Horse,
            Pos(4, 4),
            Positions((4, 4), (4, 5), (5, 4), (4, 3), (3, 4),
                (3, 6), (5, 6), (6, 3), (6, 5), (3, 2), (5, 2), (2, 3), (2, 5))
        },
        {
            PieceType.Rook,
            Pos(4, 4),
            Enumerable.Range(0, 9).Select(file => Pos(file, 4))
                .Concat(Enumerable.Range(0, 10).Select(rank => Pos(4, rank)))
                .Distinct().ToArray()
        },
        {
            PieceType.Cannon,
            Pos(4, 4),
            Enumerable.Range(0, 9).Select(file => Pos(file, 4))
                .Concat(Enumerable.Range(0, 10).Select(rank => Pos(4, rank)))
                .Distinct().ToArray()
        },
        {
            PieceType.Pawn,
            Pos(4, 5),
            Positions((4, 5), (4, 6), (5, 5), (4, 4), (3, 5))
        },
        {
            PieceType.Pawn,
            Pos(0, 0),
            Positions((0, 0), (0, 1), (1, 0))
        }
    };

    [Theory]
    [MemberData(nameof(CompleteVisibilityCases))]
    public void Each_piece_has_exact_fixed_and_route_visibility(
        PieceType type,
        Position position,
        Position[] expected)
    {
        var state = State(new PiecePlacement(position, new Piece(Side.Red, type)));

        GameEngine.ComputeVisibility(state, Side.Red).Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void Rook_includes_first_enemy_blocker_and_stops()
    {
        var state = State(
            At(4, 4, Side.Red, PieceType.Rook),
            At(4, 6, Side.Black, PieceType.Pawn));

        var expected = Enumerable.Range(0, 9).Select(file => Pos(file, 4))
            .Concat(Enumerable.Range(0, 7).Select(rank => Pos(4, rank)))
            .Distinct();
        GameEngine.ComputeVisibility(state, Side.Red).Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void Rook_includes_first_friendly_blocker_and_stops_that_ray()
    {
        var state = State(
            At(4, 4, Side.Red, PieceType.Rook),
            At(4, 6, Side.Red, PieceType.Pawn));

        var visibility = GameEngine.ComputeVisibility(state, Side.Red);
        visibility.Should().Contain(Pos(4, 6));
        visibility.Should().NotContain(Pos(4, 8));
    }

    [Fact]
    public void Cannon_sees_screen_empty_squares_second_piece_and_nothing_beyond()
    {
        var state = State(
            At(4, 4, Side.Red, PieceType.Cannon),
            At(4, 5, Side.Black, PieceType.Pawn),
            At(4, 7, Side.Black, PieceType.Horse),
            At(4, 8, Side.Black, PieceType.Rook));

        var expected = Enumerable.Range(0, 9).Select(file => Pos(file, 4))
            .Concat(Enumerable.Range(0, 8).Select(rank => Pos(4, rank)))
            .Distinct();
        GameEngine.ComputeVisibility(state, Side.Red).Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void Occupied_horse_leg_is_visible_but_its_two_targets_are_not()
    {
        var state = State(
            At(4, 4, Side.Red, PieceType.Horse),
            At(4, 5, Side.Black, PieceType.Pawn));

        GameEngine.ComputeVisibility(state, Side.Red).Should().BeEquivalentTo(
            Positions((4, 4), (4, 5), (5, 4), (4, 3), (3, 4),
                (6, 3), (6, 5), (3, 2), (5, 2), (2, 3), (2, 5)));
    }

    [Fact]
    public void Occupied_elephant_eye_is_visible_but_target_is_not()
    {
        var state = State(
            At(4, 2, Side.Red, PieceType.Elephant),
            At(5, 3, Side.Black, PieceType.Pawn));

        GameEngine.ComputeVisibility(state, Side.Red).Should().BeEquivalentTo(
            Positions((4, 2), (4, 3), (5, 2), (4, 1), (3, 2),
                (5, 3), (3, 3), (2, 4), (5, 1), (6, 0), (3, 1), (2, 0)));
    }

    [Fact]
    public void Flying_general_route_is_visible_only_when_first_piece_is_enemy_general()
    {
        var facing = State(
            At(4, 0, Side.Red, PieceType.General),
            At(4, 9, Side.Black, PieceType.General));
        GameEngine.ComputeVisibility(facing, Side.Red).Should().BeEquivalentTo(
            Enumerable.Range(0, 10).Select(rank => Pos(4, rank)).Append(Pos(3, 0)).Append(Pos(5, 0)));

        var blocked = State(
            At(4, 0, Side.Red, PieceType.General),
            At(4, 5, Side.Black, PieceType.Pawn),
            At(4, 9, Side.Black, PieceType.General));
        GameEngine.ComputeVisibility(blocked, Side.Red).Should().BeEquivalentTo(
            Positions((4, 0), (3, 0), (5, 0), (4, 1)));
    }

    [Fact]
    public void Visibility_is_deduplicated_union_of_all_friendly_pieces()
    {
        var state = State(
            At(4, 1, Side.Red, PieceType.General),
            At(4, 2, Side.Red, PieceType.Pawn));

        var visibility = GameEngine.ComputeVisibility(state, Side.Red);
        visibility.Should().OnlyHaveUniqueItems();
        visibility.Should().BeEquivalentTo(Positions(
            (4, 0), (3, 1), (4, 1), (5, 1), (3, 2), (4, 2), (5, 2), (4, 3)));
    }

    [Fact]
    public void Projection_is_recomputed_and_drops_enemy_that_leaves_visibility()
    {
        var before = State(
            At(4, 1, Side.Red, PieceType.General),
            At(4, 2, Side.Black, PieceType.Pawn),
            At(8, 9, Side.Black, PieceType.General));
        var after = State(
            At(4, 1, Side.Red, PieceType.General),
            At(4, 3, Side.Black, PieceType.Pawn),
            At(8, 9, Side.Black, PieceType.General));

        GameEngine.ProjectForPlayer(before, Side.Red).Pieces.Should().Contain(
            new VisiblePiece(Side.Black, PieceType.Pawn, Pos(4, 2)));
        GameEngine.ProjectForPlayer(after, Side.Red).Pieces.Should().NotContain(
            piece => piece.Side == Side.Black && piece.Type == PieceType.Pawn);
    }

    [Fact]
    public void Same_state_projects_different_safe_views_for_each_side()
    {
        var state = State(
            At(4, 1, Side.Red, PieceType.General),
            At(0, 0, Side.Red, PieceType.Rook),
            At(8, 8, Side.Black, PieceType.General),
            At(8, 9, Side.Black, PieceType.Rook),
            At(0, 2, Side.Black, PieceType.Pawn));

        var red = GameEngine.ProjectForPlayer(state, Side.Red);
        var black = GameEngine.ProjectForPlayer(state, Side.Black);

        red.Perspective.Should().Be(Side.Red);
        black.Perspective.Should().Be(Side.Black);
        red.VisibleSquares.Should().NotBeEquivalentTo(black.VisibleSquares);
        red.Pieces.Should().Contain(piece => piece == new VisiblePiece(Side.Black, PieceType.Pawn, Pos(0, 2)));
        red.Pieces.Should().NotContain(piece => piece.Position == Pos(8, 9));
        black.Pieces.Should().NotContain(piece => piece.Position == Pos(4, 1));
    }

    [Fact]
    public void Candidate_moves_are_only_returned_to_side_whose_turn_it_is()
    {
        var state = State(
            At(4, 1, Side.Red, PieceType.General),
            At(8, 8, Side.Black, PieceType.General));

        GameEngine.ProjectForPlayer(state, Side.Red).CandidateMoves.Should().NotBeEmpty();
        GameEngine.ProjectForPlayer(state, Side.Black).CandidateMoves.Should().BeEmpty();
    }

    [Fact]
    public void Finished_projection_reveals_complete_final_board()
    {
        var state = State(
            At(0, 0, Side.Red, PieceType.Rook),
            At(0, 4, Side.Black, PieceType.General),
            At(8, 9, Side.Black, PieceType.Horse));
        var finished = GameEngine.ApplyMove(state, new Move(Pos(0, 0), Pos(0, 4))).State;

        var view = GameEngine.ProjectForPlayer(finished, Side.Red);

        view.VisibleSquares.Should().HaveCount(GameState.BoardSize);
        view.Pieces.Should().Contain(new VisiblePiece(Side.Black, PieceType.Horse, Pos(8, 9)));
        view.CandidateMoves.Should().BeEmpty();
    }

    [Fact]
    public void Serialized_view_cannot_contain_hidden_piece_or_internal_threat_information()
    {
        var state = State(
            At(4, 0, Side.Red, PieceType.General),
            At(4, 4, Side.Black, PieceType.Rook),
            At(8, 9, Side.Black, PieceType.General),
            At(4, 1, Side.Black, PieceType.Pawn));
        GameEngine.IsGeneralThreatened(state, Side.Red).Should().BeTrue();

        var json = Serialize(GameEngine.ProjectForPlayer(state, Side.Red));

        var lowerJson = json.ToLowerInvariant();
        json.Should().NotContain("Rook");
        json.Should().NotContain("\"Rank\":4");
        lowerJson.Should().NotContain("checkedside");
        lowerJson.Should().NotContain("isincheck");
        lowerJson.Should().NotContain("generalthreatened");
        lowerJson.Should().NotContain("attacker");
        json.Should().NotContain("\"Id\"");
    }

    [Fact]
    public void Hidden_internal_threat_does_not_change_visible_protocol_or_candidates()
    {
        var threatened = State(
            At(4, 0, Side.Red, PieceType.General),
            At(4, 4, Side.Black, PieceType.Rook),
            At(8, 9, Side.Black, PieceType.General));
        var safe = State(
            At(4, 0, Side.Red, PieceType.General),
            At(8, 4, Side.Black, PieceType.Rook),
            At(8, 9, Side.Black, PieceType.General));

        GameEngine.IsGeneralThreatened(threatened, Side.Red).Should().BeTrue();
        GameEngine.IsGeneralThreatened(safe, Side.Red).Should().BeFalse();
        Serialize(GameEngine.ProjectForPlayer(threatened, Side.Red)).Should()
            .Be(Serialize(GameEngine.ProjectForPlayer(safe, Side.Red)));
    }

    private static string Serialize(GameView view)
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter());
        return JsonSerializer.Serialize(view, options);
    }

    private static GameState State(params PiecePlacement[] pieces) => GameState.Create(pieces);

    private static PiecePlacement At(int file, int rank, Side side, PieceType type) =>
        new(Pos(file, rank), new Piece(side, type));

    private static Position[] Positions(params (int File, int Rank)[] positions) =>
        positions.Select(position => Pos(position.File, position.Rank)).ToArray();

    private static Position Pos(int file, int rank) => new(file, rank);
}
