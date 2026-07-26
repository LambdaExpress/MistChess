using FluentAssertions;

namespace MistChess.Domain.Tests;

public sealed class GameEngineTests
{
    [Fact]
    public void Initial_state_has_fixed_board_and_standard_layout()
    {
        var state = GameState.CreateInitial();

        state.Pieces.Should().HaveCount(32);
        state.SideToMove.Should().Be(Side.Red);
        state.RuleVersion.Should().Be(GameState.CurrentRuleVersion);
        state.GetPiece(Pos(0, 0)).Should().Be(new Piece(Side.Red, PieceType.Rook));
        state.GetPiece(Pos(4, 0)).Should().Be(new Piece(Side.Red, PieceType.General));
        state.GetPiece(Pos(1, 2)).Should().Be(new Piece(Side.Red, PieceType.Cannon));
        state.GetPiece(Pos(8, 3)).Should().Be(new Piece(Side.Red, PieceType.Pawn));
        state.GetPiece(Pos(0, 9)).Should().Be(new Piece(Side.Black, PieceType.Rook));
        state.GetPiece(Pos(4, 9)).Should().Be(new Piece(Side.Black, PieceType.General));
        state.GetPiece(Pos(7, 7)).Should().Be(new Piece(Side.Black, PieceType.Cannon));
        state.GetPiece(Pos(0, 6)).Should().Be(new Piece(Side.Black, PieceType.Pawn));
        Enumerable.Range(0, GameState.BoardSize)
            .Select(index => new Position(index % 9, index / 9))
            .Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void General_and_advisor_stay_inside_their_palace()
    {
        var state = State(
            At(3, 0, Side.Red, PieceType.General),
            At(5, 2, Side.Red, PieceType.Advisor));

        Destinations(state, Pos(3, 0)).Should().BeEquivalentTo([Pos(4, 0), Pos(3, 1)]);
        Destinations(state, Pos(5, 2)).Should().BeEquivalentTo([Pos(4, 1)]);
    }

    [Fact]
    public void Elephant_cannot_cross_river_or_move_through_an_occupied_eye()
    {
        var unobstructed = State(At(4, 2, Side.Red, PieceType.Elephant));
        var obstructed = State(
            At(4, 2, Side.Red, PieceType.Elephant),
            At(5, 3, Side.Black, PieceType.Pawn));

        Destinations(unobstructed, Pos(4, 2)).Should().BeEquivalentTo(
            [Pos(2, 0), Pos(6, 0), Pos(2, 4), Pos(6, 4)]);
        Destinations(obstructed, Pos(4, 2)).Should().NotContain(Pos(6, 4));

        var riverEdge = State(At(2, 4, Side.Red, PieceType.Elephant));
        Destinations(riverEdge, Pos(2, 4)).Should().OnlyContain(position => position.Rank <= 4);
    }

    [Fact]
    public void Horse_leg_blocks_both_corresponding_destinations()
    {
        var state = State(
            At(4, 4, Side.Red, PieceType.Horse),
            At(4, 5, Side.Black, PieceType.Pawn));

        Destinations(state, Pos(4, 4)).Should().BeEquivalentTo(
            [Pos(6, 5), Pos(6, 3), Pos(5, 2), Pos(3, 2), Pos(2, 3), Pos(2, 5)]);
    }

    [Fact]
    public void Rook_stops_at_first_piece_in_every_direction()
    {
        var state = State(
            At(4, 4, Side.Red, PieceType.Rook),
            At(4, 6, Side.Black, PieceType.Pawn),
            At(6, 4, Side.Black, PieceType.Pawn),
            At(4, 2, Side.Red, PieceType.Pawn),
            At(2, 4, Side.Red, PieceType.Pawn));

        Destinations(state, Pos(4, 4)).Should().BeEquivalentTo(
            [Pos(4, 3), Pos(4, 5), Pos(4, 6), Pos(3, 4), Pos(5, 4), Pos(6, 4)]);
    }

    [Fact]
    public void Cannon_moves_before_screen_and_captures_only_first_piece_after_one_screen()
    {
        var state = State(
            At(4, 4, Side.Red, PieceType.Cannon),
            At(4, 5, Side.Red, PieceType.Pawn),
            At(4, 7, Side.Black, PieceType.Horse),
            At(4, 8, Side.Black, PieceType.Rook));

        var destinations = Destinations(state, Pos(4, 4));
        destinations.Should().Contain(Pos(4, 7));
        destinations.Should().NotContain(position =>
            position == Pos(4, 5) || position == Pos(4, 6) || position == Pos(4, 8));

        var noScreen = State(
            At(4, 4, Side.Red, PieceType.Cannon),
            At(4, 7, Side.Black, PieceType.Horse));
        Destinations(noScreen, Pos(4, 4)).Should().Contain(Pos(4, 5));
        Destinations(noScreen, Pos(4, 4)).Should().Contain(Pos(4, 6));
        Destinations(noScreen, Pos(4, 4)).Should().NotContain(position =>
            position == Pos(4, 7) || position == Pos(4, 8));
    }

    [Fact]
    public void Pawns_only_move_forward_until_they_cross_the_river()
    {
        var redBefore = State(At(4, 4, Side.Red, PieceType.Pawn));
        var redAfter = State(At(4, 5, Side.Red, PieceType.Pawn));
        var blackAfter = State(At(4, 4, Side.Black, PieceType.Pawn));

        Destinations(redBefore, Pos(4, 4)).Should().Equal(Pos(4, 5));
        Destinations(redAfter, Pos(4, 5)).Should().BeEquivalentTo([Pos(4, 6), Pos(3, 5), Pos(5, 5)]);
        Destinations(blackAfter, Pos(4, 4)).Should().BeEquivalentTo([Pos(4, 3), Pos(3, 4), Pos(5, 4)]);
    }

    [Fact]
    public void Every_piece_rejects_an_own_occupied_destination_and_board_edges()
    {
        var cases = new[]
        {
            (PieceType.General, Pos(4, 1), Pos(4, 2)),
            (PieceType.Advisor, Pos(4, 1), Pos(5, 2)),
            (PieceType.Elephant, Pos(2, 0), Pos(4, 2)),
            (PieceType.Horse, Pos(0, 0), Pos(1, 2)),
            (PieceType.Rook, Pos(0, 0), Pos(0, 1)),
            (PieceType.Cannon, Pos(0, 0), Pos(0, 1)),
            (PieceType.Pawn, Pos(0, 3), Pos(0, 4))
        };

        foreach (var (type, from, target) in cases)
        {
            var state = State(
                new PiecePlacement(from, new Piece(Side.Red, type)),
                new PiecePlacement(target, new Piece(Side.Red, PieceType.Pawn)));
            Destinations(state, from).Should().NotContain(target, $"{type} cannot capture its own piece");
            Destinations(state, from).Should().OnlyContain(position =>
                position.File < GameState.BoardFiles && position.Rank < GameState.BoardRanks);
        }
    }

    [Fact]
    public void Cannon_cannot_capture_an_own_piece_behind_a_screen()
    {
        var state = State(
            At(0, 0, Side.Red, PieceType.Cannon),
            At(0, 1, Side.Black, PieceType.Pawn),
            At(0, 2, Side.Red, PieceType.Horse));

        Destinations(state, Pos(0, 0)).Should().NotContain(Pos(0, 2));
    }

    [Fact]
    public void Facing_generals_continue_play_and_can_capture_by_flying_general()
    {
        var state = State(
            At(4, 0, Side.Red, PieceType.General),
            At(4, 9, Side.Black, PieceType.General));

        GameEngine.IsGeneralThreatened(state, Side.Red).Should().BeTrue();
        GameEngine.IsGeneralThreatened(state, Side.Black).Should().BeTrue();
        Destinations(state, Pos(4, 0)).Should().Contain(Pos(4, 9));

        var application = GameEngine.ApplyMove(state, new Move(Pos(4, 0), Pos(4, 9)));
        application.State.Status.Should().Be(GameStatus.Finished);
        application.State.Result.Should().Be(new GameResult(Side.Red, GameEndReason.GeneralCaptured));
    }

    public static TheoryData<PiecePlacement[]> GeneralThreatCases => new()
    {
        new[] { At(4, 0, Side.Red, PieceType.General), At(4, 5, Side.Black, PieceType.Rook) },
        new[] { At(4, 0, Side.Red, PieceType.General), At(3, 2, Side.Black, PieceType.Horse) },
        new[] { At(4, 0, Side.Red, PieceType.General), At(4, 5, Side.Black, PieceType.Cannon), At(4, 3, Side.Red, PieceType.Pawn) },
        new[] { At(4, 0, Side.Red, PieceType.General), At(4, 1, Side.Black, PieceType.Pawn) },
        new[] { At(4, 8, Side.Red, PieceType.General), At(3, 9, Side.Black, PieceType.Advisor) },
        new[] { At(4, 7, Side.Red, PieceType.General), At(2, 9, Side.Black, PieceType.Elephant) },
        new[] { At(4, 0, Side.Red, PieceType.General), At(4, 9, Side.Black, PieceType.General) }
    };

    [Theory]
    [MemberData(nameof(GeneralThreatCases))]
    public void Every_piece_type_can_threaten_a_general(PiecePlacement[] placements)
    {
        GameEngine.IsGeneralThreatened(State(placements), Side.Red).Should().BeTrue();
    }

    [Fact]
    public void Threatened_general_does_not_filter_or_reject_moves()
    {
        var state = State(
            At(4, 0, Side.Red, PieceType.General),
            At(4, 4, Side.Black, PieceType.Rook),
            At(8, 9, Side.Black, PieceType.General));

        Destinations(state, Pos(4, 0)).Should().Contain(Pos(3, 0));
        Destinations(state, Pos(4, 0)).Should().Contain(Pos(5, 0));
        Destinations(state, Pos(4, 0)).Should().Contain(Pos(4, 1));
        var application = GameEngine.ApplyMove(state, new Move(Pos(4, 0), Pos(4, 1)));
        application.State.Status.Should().Be(GameStatus.Playing);
    }

    [Fact]
    public void Capturing_general_with_an_ordinary_piece_wins_immediately()
    {
        var state = State(
            At(0, 0, Side.Red, PieceType.Rook),
            At(0, 4, Side.Black, PieceType.General));

        var application = GameEngine.ApplyMove(state, new Move(Pos(0, 0), Pos(0, 4)));

        application.Event.CapturedPiece.Should().Be(new Piece(Side.Black, PieceType.General));
        application.State.Result.Should().Be(new GameResult(Side.Red, GameEndReason.GeneralCaptured));
    }

    [Fact]
    public void Side_with_no_legal_move_loses()
    {
        var state = GameState.Create([], Side.Black);

        GameEngine.HasAnyMove(state, Side.Black).Should().BeFalse();
        GameEngine.EvaluateResult(state).Should().Be(new GameResult(Side.Red, GameEndReason.NoLegalMove));
    }

    [Fact]
    public void Third_occurrence_of_layout_and_turn_is_a_draw()
    {
        var state = State(
            At(0, 0, Side.Red, PieceType.Rook),
            At(4, 0, Side.Red, PieceType.General),
            At(8, 9, Side.Black, PieceType.Rook),
            At(4, 9, Side.Black, PieceType.General));
        var cycle = new[]
        {
            new Move(Pos(0, 0), Pos(0, 1)),
            new Move(Pos(8, 9), Pos(8, 8)),
            new Move(Pos(0, 1), Pos(0, 0)),
            new Move(Pos(8, 8), Pos(8, 9))
        };

        for (var repetition = 0; repetition < 2; repetition++)
        {
            foreach (var move in cycle)
            {
                state = GameEngine.ApplyMove(state, move).State;
            }
        }

        state.Result.Should().Be(new GameResult(null, GameEndReason.Repetition));
    }

    [Fact]
    public void One_hundred_twentieth_no_progress_half_move_is_a_draw_and_pawn_resets_counter()
    {
        var state = GameState.Create(
            [
                At(0, 0, Side.Red, PieceType.Rook),
                At(4, 0, Side.Red, PieceType.General),
                At(4, 9, Side.Black, PieceType.General)
            ],
            noProgressHalfMoveCount: 119);

        GameEngine.ApplyMove(state, new Move(Pos(0, 0), Pos(0, 1))).State.Result
            .Should().Be(new GameResult(null, GameEndReason.NoProgress));

        var pawnState = GameState.Create(
            [At(0, 3, Side.Red, PieceType.Pawn), At(4, 9, Side.Black, PieceType.General)],
            noProgressHalfMoveCount: 119);
        GameEngine.ApplyMove(pawnState, new Move(Pos(0, 3), Pos(0, 4))).State.NoProgressHalfMoveCount
            .Should().Be(0);

        var captureState = GameState.Create(
            [
                At(0, 0, Side.Red, PieceType.Rook),
                At(0, 1, Side.Black, PieceType.Pawn),
                At(4, 9, Side.Black, PieceType.General)
            ],
            noProgressHalfMoveCount: 119);
        GameEngine.ApplyMove(captureState, new Move(Pos(0, 0), Pos(0, 1))).State.NoProgressHalfMoveCount
            .Should().Be(0);
    }

    [Theory]
    [InlineData(GameEndReason.Resignation, Side.Red)]
    [InlineData(GameEndReason.Timeout, Side.Black)]
    [InlineData(GameEndReason.AgreedDraw, null)]
    public void External_end_reasons_finish_the_domain_state(GameEndReason reason, Side? winner)
    {
        var state = State(
            At(4, 0, Side.Red, PieceType.General),
            At(4, 9, Side.Black, PieceType.General));

        var finished = state.Finish(new GameResult(winner, reason));

        finished.Status.Should().Be(GameStatus.Finished);
        finished.Result.Should().Be(new GameResult(winner, reason));
        finished.Pieces.Should().BeEquivalentTo(state.Pieces);
        GameEngine.ProjectForPlayer(finished, Side.Red).Pieces.Should().HaveCount(state.Pieces.Count);
    }

    [Fact]
    public void Position_key_is_deterministic_and_includes_side_to_move()
    {
        var placements = new[]
        {
            At(4, 0, Side.Red, PieceType.General),
            At(2, 6, Side.Black, PieceType.Pawn),
            At(8, 9, Side.Black, PieceType.Rook)
        };
        var first = GameState.Create(placements, Side.Red);
        var reordered = GameState.Create(placements.Reverse(), Side.Red);
        var otherTurn = GameState.Create(placements, Side.Black);

        GameEngine.ComputePositionKey(first).Should().Be(GameEngine.ComputePositionKey(reordered));
        GameEngine.ComputePositionKey(otherTurn).Should().NotBe(first.PositionKey);
        first.PositionKey.Should().HaveLength(GameState.BoardSize + 2);
    }

    private static GameState State(params PiecePlacement[] pieces) => GameState.Create(pieces);

    private static Position[] Destinations(GameState state, Position from) =>
        GameEngine.GenerateMoves(state, from).Select(move => move.To).ToArray();

    private static PiecePlacement At(int file, int rank, Side side, PieceType type) =>
        new(Pos(file, rank), new Piece(side, type));

    private static Position Pos(int file, int rank) => new(file, rank);
}
