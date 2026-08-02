using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MistChess.Api.Application;
using MistChess.Api.Contracts;
using MistChess.Api.Tests.Infrastructure;
using MistChess.Domain;
using Npgsql;
using ApiGameView = MistChess.Api.Contracts.GameView;
using ApiPosition = MistChess.Api.Contracts.Position;

namespace MistChess.Api.Tests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class PostgresApiWorkflowTests(PostgresDatabaseFixture database) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private MistChessWebApplicationFactory _factory = null!;

    public async Task InitializeAsync()
    {
        await database.ResetAsync();
        _factory = new MistChessWebApplicationFactory(database.ConnectionString);
    }

    [Fact]
    public async Task Room_game_commands_are_authoritative_idempotent_and_private()
    {
        using var first = await CreatePlayerAsync();
        using var second = await CreatePlayerAsync();
        using var outsider = await CreatePlayerAsync();

        var created = await PostAsync<RoomView>(first, "/api/rooms", new CreateRoomRequest(GameState.CurrentRuleVersion, null));
        var joined = await PostAsync<RoomView>(second, $"/api/rooms/{created.Code}/join", body: null);
        joined.Status.Should().Be(GameStatus.WaitingForReady);

        var firstReady = await PostAsync<RoomView>(first, $"/api/rooms/{created.Code}/ready", new SetReadyRequest(true));
        firstReady.GameId.Should().BeNull();
        var started = await PostAsync<RoomView>(second, $"/api/rooms/{created.Code}/ready", new SetReadyRequest(true));
        started.GameId.Should().NotBeNull();
        started.Status.Should().Be(GameStatus.Playing);
        started.Players.Select(value => value.Side).Should().BeEquivalentTo(new Side?[] { Side.Red, Side.Black });

        var gameId = started.GameId!.Value;
        var firstView = await GetAsync<ApiGameView>(first, $"/api/games/{gameId:D}");
        var secondView = await GetAsync<ApiGameView>(second, $"/api/games/{gameId:D}");
        firstView.Perspective.Should().NotBe(secondView.Perspective);
        firstView.Pieces.Should().NotBeEquivalentTo(secondView.Pieces);

        var firstJson = JsonSerializer.Serialize(firstView, JsonOptions);
        firstJson.Should().NotContain("checkedSide");
        firstJson.Should().NotContain("isInCheck");
        firstJson.Should().NotContain("generalThreatened");

        var currentClient = firstView.Perspective == Side.Red ? first : second;
        var currentView = firstView.Perspective == Side.Red ? firstView : secondView;
        var candidate = currentView.CandidateMoves.First(value => value.Destinations.Count > 0);
        var moveRequest = new MoveRequest(candidate.From, candidate.Destinations[0], currentView.Version, "move-1");
        var moved = await PostAsync<ApiGameView>(currentClient, $"/api/games/{gameId:D}/moves", moveRequest);
        moved.Version.Should().Be(1);

        var retried = await PostAsync<ApiGameView>(currentClient, $"/api/games/{gameId:D}/moves", moveRequest);
        retried.Should().BeEquivalentTo(moved);

        var staleResponse = await PostResponseAsync(
            currentClient,
            $"/api/games/{gameId:D}/moves",
            moveRequest with { ClientMoveId = "move-stale" });
        staleResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadAsync<ErrorResponse>(staleResponse)).Code.Should().Be("STALE_VERSION");

        var nextClient = ReferenceEquals(currentClient, first) ? second : first;
        var firstIllegal = await PostResponseAsync(
            nextClient,
            $"/api/games/{gameId:D}/moves",
            new MoveRequest(new ApiPosition(4, 4), new ApiPosition(4, 5), moved.Version, "illegal-1"));
        var secondIllegal = await PostResponseAsync(
            nextClient,
            $"/api/games/{gameId:D}/moves",
            new MoveRequest(new ApiPosition(3, 4), new ApiPosition(3, 5), moved.Version, "illegal-2"));
        firstIllegal.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        secondIllegal.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await firstIllegal.Content.ReadAsStringAsync()).Should().Be(await secondIllegal.Content.ReadAsStringAsync());

        var activeReplay = await first.GetAsync($"/api/games/{gameId:D}/replay");
        activeReplay.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var forbidden = await outsider.GetAsync($"/api/games/{gameId:D}");
        var absent = await outsider.GetAsync($"/api/games/{Guid.NewGuid():D}");
        forbidden.StatusCode.Should().Be(HttpStatusCode.NotFound);
        absent.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await forbidden.Content.ReadAsStringAsync()).Should().Be(await absent.Content.ReadAsStringAsync());

        var resigned = await PostAsync<ApiGameView>(currentClient, $"/api/games/{gameId:D}/resign", body: null);
        resigned.Status.Should().Be(GameStatus.Finished);
        resigned.Result!.Reason.Should().Be(GameResultReason.Resignation);
        resigned.VisibleSquares.Should().HaveCount(90);
        resigned.Pieces.Should().HaveCount(32);

        var firstReplay = await GetAsync<HistoricalReplayView>(first, $"/api/games/{gameId:D}/replay");
        var secondReplay = await GetAsync<HistoricalReplayView>(second, $"/api/games/{gameId:D}/replay");
        firstReplay.Result.Should().BeEquivalentTo(secondReplay.Result);
        firstReplay.Frames.Should().HaveCount(3);
        firstReplay.Frames[^1].Views.Omniscient.Move.Should().BeNull();
    }

    [Fact]
    public async Task Players_can_leave_an_unstarted_room_and_creator_leave_closes_it()
    {
        using var creator = await CreatePlayerAsync();
        using var guest = await CreatePlayerAsync();

        var created = await PostAsync<RoomView>(
            creator,
            "/api/rooms",
            new CreateRoomRequest(GameState.CurrentRuleVersion, null));
        await PostAsync<RoomView>(guest, $"/api/rooms/{created.Code}/join", body: null);
        await PostAsync<RoomView>(
            creator,
            $"/api/rooms/{created.Code}/ready",
            new SetReadyRequest(true));

        var guestLeave = await guest.DeleteAsync($"/api/rooms/{created.Code}/members/me");
        guestLeave.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var rejoined = await PostAsync<RoomView>(guest, $"/api/rooms/{created.Code}/join", body: null);
        rejoined.Status.Should().Be(GameStatus.WaitingForReady);
        rejoined.Players.Should().OnlyContain(value => !value.IsReady);

        (await guest.DeleteAsync($"/api/rooms/{created.Code}/members/me"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await creator.DeleteAsync($"/api/rooms/{created.Code}/members/me"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await PostResponseAsync(guest, $"/api/rooms/{created.Code}/join", body: null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Accepted_draw_stays_accepted_and_repeated_accept_returns_the_same_terminal_game()
    {
        using var first = await CreatePlayerAsync();
        using var second = await CreatePlayerAsync();
        var gameId = await CreateStartedRoomGameAsync(first, second);

        var offered = await PostAsync<DrawOfferView>(
            first,
            $"/api/games/{gameId:D}/draw-offers",
            body: null);
        offered.Id.Should().NotBeEmpty();
        offered.Status.Should().Be(DrawOfferStatus.Pending);
        offered.Revision.Should().BeGreaterThan(0);

        var rejected = await PostAsync<DrawOfferView>(
            second,
            $"/api/games/{gameId:D}/draw-offers/reject",
            body: null);
        rejected.Id.Should().Be(offered.Id);
        rejected.Status.Should().Be(DrawOfferStatus.Rejected);
        rejected.Revision.Should().Be(offered.Revision + 1);

        var acceptedOffer = await PostAsync<DrawOfferView>(
            first,
            $"/api/games/{gameId:D}/draw-offers",
            body: null);
        acceptedOffer.Id.Should().NotBe(offered.Id);
        acceptedOffer.Revision.Should().Be(rejected.Revision + 1);

        var accepted = await PostAsync<ApiGameView>(
            second,
            $"/api/games/{gameId:D}/draw-offers/accept",
            body: null);
        accepted.Status.Should().Be(GameStatus.Finished);
        accepted.Result.Should().Be(new GameResultView(null, GameResultReason.AgreedDraw));
        accepted.DrawOffer.Should().BeNull();
        accepted.NegotiationVersion.Should().Be(acceptedOffer.Revision + 1);
        accepted.VisibleSquares.Should().HaveCount(90);

        var repeated = await PostAsync<ApiGameView>(
            second,
            $"/api/games/{gameId:D}/draw-offers/accept",
            body: null);
        repeated.Should().BeEquivalentTo(accepted);
        var authoritative = await GetAsync<ApiGameView>(first, $"/api/games/{gameId:D}");
        authoritative.Result.Should().Be(accepted.Result);
        authoritative.DrawOffer.Should().BeNull();
        authoritative.NegotiationVersion.Should().Be(accepted.NegotiationVersion);

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT accepted.status,
                   rejected.status,
                   game.negotiation_version,
                   game.result_reason
            FROM draw_offers AS accepted
            INNER JOIN games AS game ON game.id = accepted.game_id
            INNER JOIN draw_offers AS rejected ON rejected.id = @rejected_id
            WHERE accepted.id = @accepted_id
              AND accepted.game_id = @game_id
              AND rejected.game_id = accepted.game_id
            """,
            connection);
        command.Parameters.AddWithValue("game_id", gameId);
        command.Parameters.AddWithValue("accepted_id", acceptedOffer.Id);
        command.Parameters.AddWithValue("rejected_id", rejected.Id);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be("Accepted");
        reader.GetString(1).Should().Be("Rejected");
        reader.GetInt64(2).Should().Be(accepted.NegotiationVersion);
        reader.GetString(3).Should().Be(GameResultReason.AgreedDraw.ToString());
        (await reader.ReadAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Latest_move_is_the_authoritative_last_action_and_only_its_mover_can_request_takeback()
    {
        using var first = await CreatePlayerAsync();
        using var second = await CreatePlayerAsync();
        var gameId = await CreateStartedRoomGameAsync(first, second);
        var initialFirst = await GetAsync<ApiGameView>(first, $"/api/games/{gameId:D}");
        var initialSecond = await GetAsync<ApiGameView>(second, $"/api/games/{gameId:D}");
        initialFirst.LastAction.Should().BeNull();
        initialSecond.LastAction.Should().BeNull();
        initialFirst.CanRequestTakeback.Should().BeFalse();
        initialSecond.CanRequestTakeback.Should().BeFalse();

        var turn = await GetCurrentTurnAsync(first, second, gameId);
        var moved = await PlayCandidateMoveAsync(gameId, turn, "last-action-move");
        var expectedAction = new GameActionView(moved.Version, GameActionKind.Move, turn.MoverView.SideToMove);
        moved.LastAction.Should().Be(expectedAction);
        moved.CanRequestTakeback.Should().BeTrue();

        var moverView = await GetAsync<ApiGameView>(turn.Mover, $"/api/games/{gameId:D}");
        var opponentView = await GetAsync<ApiGameView>(turn.Opponent, $"/api/games/{gameId:D}");
        moverView.LastAction.Should().Be(expectedAction);
        opponentView.LastAction.Should().Be(expectedAction);
        moverView.CanRequestTakeback.Should().BeTrue();
        opponentView.CanRequestTakeback.Should().BeFalse();

        using var unavailable = await PostResponseAsync(
            turn.Opponent,
            $"/api/games/{gameId:D}/takeback-requests",
            new CreateTakebackRequest(moved.Version, "opponent-cannot-take-back"));
        unavailable.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadAsync<ErrorResponse>(unavailable)).Code.Should().Be("TAKEBACK_NOT_AVAILABLE");

        var command = new CreateTakebackRequest(moved.Version, "authoritative-create");
        var created = await PostAsync<TakebackRequestView>(
            turn.Mover,
            $"/api/games/{gameId:D}/takeback-requests",
            command);
        created.Id.Should().NotBeEmpty();
        created.Status.Should().Be(TakebackRequestStatus.Pending);
        created.RequestedBy.Should().Be(turn.MoverView.SideToMove);
        created.RequestedPly.Should().Be(1);
        created.RequestedAtVersion.Should().Be(moved.Version);
        created.ResolvedAtVersion.Should().BeNull();
        created.Revision.Should().Be(moved.NegotiationVersion + 1);

        var repeated = await PostAsync<TakebackRequestView>(
            turn.Mover,
            $"/api/games/{gameId:D}/takeback-requests",
            command);
        repeated.Should().BeEquivalentTo(created);
        var afterForMover = await GetAsync<ApiGameView>(turn.Mover, $"/api/games/{gameId:D}");
        var afterForOpponent = await GetAsync<ApiGameView>(turn.Opponent, $"/api/games/{gameId:D}");
        afterForMover.TakebackRequest.Should().BeEquivalentTo(created);
        afterForOpponent.TakebackRequest.Should().BeEquivalentTo(created);
        afterForMover.NegotiationVersion.Should().Be(created.Revision);
        afterForOpponent.NegotiationVersion.Should().Be(created.Revision);
        afterForMover.CanRequestTakeback.Should().BeFalse();
        afterForOpponent.CanRequestTakeback.Should().BeFalse();

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var audit = new NpgsqlCommand(
            """
            SELECT game.last_action_version,
                   game.last_action_kind,
                   game.last_action_actor,
                   game.negotiation_version,
                   game.takeback_window_consumed,
                   (SELECT count(*)
                    FROM takeback_requests AS request
                    WHERE request.game_id = game.id AND request.status = 'Pending')
            FROM games AS game
            WHERE game.id = @game_id
            """,
            connection);
        audit.Parameters.AddWithValue("game_id", gameId);
        await using var reader = await audit.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt64(0).Should().Be(expectedAction.Version);
        reader.GetString(1).Should().Be("move");
        reader.GetString(2).Should().Be(expectedAction.Actor.ToString());
        reader.GetInt64(3).Should().Be(created.Revision);
        reader.GetBoolean(4).Should().BeTrue();
        reader.GetInt64(5).Should().Be(1);
        (await reader.ReadAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Capture_last_action_and_replayed_ply_remain_authoritative_after_takeback_and_replay()
    {
        using var first = await CreatePlayerAsync();
        using var second = await CreatePlayerAsync();
        var gameId = await CreateStartedRoomGameAsync(first, second);
        var redTurn = await GetCurrentTurnAsync(first, second, gameId);
        redTurn.MoverView.SideToMove.Should().Be(Side.Red);
        await PostAsync<ApiGameView>(
            redTurn.Mover,
            $"/api/games/{gameId:D}/moves",
            new MoveRequest(
                new ApiPosition(0, 3),
                new ApiPosition(0, 4),
                redTurn.MoverView.Version,
                "capture-red-advance"));

        var blackTurn = await GetCurrentTurnAsync(first, second, gameId);
        blackTurn.MoverView.SideToMove.Should().Be(Side.Black);
        await PostAsync<ApiGameView>(
            blackTurn.Mover,
            $"/api/games/{gameId:D}/moves",
            new MoveRequest(
                new ApiPosition(0, 6),
                new ApiPosition(0, 5),
                blackTurn.MoverView.Version,
                "capture-black-advance"));

        var beforeCaptureFirst = await GetAsync<ApiGameView>(first, $"/api/games/{gameId:D}");
        var beforeCaptureSecond = await GetAsync<ApiGameView>(second, $"/api/games/{gameId:D}");
        var captureTurn = await GetCurrentTurnAsync(first, second, gameId);
        captureTurn.Mover.Should().BeSameAs(redTurn.Mover);
        var captured = await PostAsync<ApiGameView>(
            captureTurn.Mover,
            $"/api/games/{gameId:D}/moves",
            new MoveRequest(
                new ApiPosition(0, 4),
                new ApiPosition(0, 5),
                captureTurn.MoverView.Version,
                "capture-pawn"));
        var captureAction = new GameActionView(captured.Version, GameActionKind.Capture, Side.Red);
        captured.LastAction.Should().Be(captureAction);
        captured.CaptureSummary.BlackLost.Should().ContainSingle(piece => piece == PieceType.Pawn);
        (await GetAsync<ApiGameView>(first, $"/api/games/{gameId:D}")).LastAction.Should().Be(captureAction);
        (await GetAsync<ApiGameView>(second, $"/api/games/{gameId:D}")).LastAction.Should().Be(captureAction);

        var pending = await PostAsync<TakebackRequestView>(
            captureTurn.Mover,
            $"/api/games/{gameId:D}/takeback-requests",
            new CreateTakebackRequest(captured.Version, "capture-takeback"));
        var accepted = await PostAsync<ApiGameView>(
            captureTurn.Opponent,
            $"/api/games/{gameId:D}/takeback-requests/{pending.Id:D}/accept",
            body: null);
        accepted.Version.Should().Be(captured.Version + 1);
        accepted.SideToMove.Should().Be(Side.Red);
        accepted.LastAction.Should().Be(new GameActionView(
            accepted.Version,
            GameActionKind.TakebackAccepted,
            Side.Red));

        var restoredFirst = await GetAsync<ApiGameView>(first, $"/api/games/{gameId:D}");
        var restoredSecond = await GetAsync<ApiGameView>(second, $"/api/games/{gameId:D}");
        restoredFirst.Pieces.Should().BeEquivalentTo(beforeCaptureFirst.Pieces);
        restoredSecond.Pieces.Should().BeEquivalentTo(beforeCaptureSecond.Pieces);
        restoredFirst.CaptureSummary.Should().BeEquivalentTo(beforeCaptureFirst.CaptureSummary);
        restoredSecond.CaptureSummary.Should().BeEquivalentTo(beforeCaptureSecond.CaptureSummary);
        restoredFirst.CandidateMoves.Should().BeEquivalentTo(beforeCaptureFirst.CandidateMoves);
        restoredSecond.CandidateMoves.Should().BeEquivalentTo(beforeCaptureSecond.CandidateMoves);

        var replayedTurn = await GetCurrentTurnAsync(first, second, gameId);
        var replayedCapture = await PostAsync<ApiGameView>(
            replayedTurn.Mover,
            $"/api/games/{gameId:D}/moves",
            new MoveRequest(
                new ApiPosition(0, 4),
                new ApiPosition(0, 5),
                replayedTurn.MoverView.Version,
                "capture-pawn-replayed"));
        replayedCapture.LastAction.Should().Be(new GameActionView(
            replayedCapture.Version,
            GameActionKind.Capture,
            Side.Red));

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var audit = new NpgsqlCommand(
            """
            SELECT game.version,
                   game.last_action_version,
                   game.last_action_kind,
                   game.last_action_actor,
                   request.status,
                   (SELECT count(*) FROM moves WHERE game_id = game.id AND ply = 3),
                   (SELECT count(*) FROM moves WHERE game_id = game.id AND ply = 3 AND reverted_at IS NULL),
                   (SELECT count(*) FROM moves WHERE game_id = game.id AND reverted_by_takeback_request_id = request.id),
                   (SELECT captured_piece_type FROM moves WHERE game_id = game.id AND ply = 3 AND reverted_at IS NULL)
            FROM games AS game
            INNER JOIN takeback_requests AS request ON request.game_id = game.id
            WHERE game.id = @game_id AND request.id = @request_id
            """,
            connection))
        {
            audit.Parameters.AddWithValue("game_id", gameId);
            audit.Parameters.AddWithValue("request_id", pending.Id);
            await using var reader = await audit.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt64(0).Should().Be(replayedCapture.Version);
            reader.GetInt64(1).Should().Be(replayedCapture.Version);
            reader.GetString(2).Should().Be("capture");
            reader.GetString(3).Should().Be(Side.Red.ToString());
            reader.GetString(4).Should().Be("Accepted");
            reader.GetInt64(5).Should().Be(2);
            reader.GetInt64(6).Should().Be(1);
            reader.GetInt64(7).Should().Be(1);
            reader.GetString(8).Should().Be(PieceType.Pawn.ToString());
            (await reader.ReadAsync()).Should().BeFalse();
        }

        await PostAsync<ApiGameView>(first, $"/api/games/{gameId:D}/resign", body: null);
        var history = await GetAsync<HistoricalGamesPageView>(first, "/api/games/history?limit=10");
        history.Games.Should().ContainSingle(game => game.GameId == gameId && game.PlyCount == 3);
        var replay = await GetAsync<HistoricalReplayView>(first, $"/api/games/{gameId:D}/replay");
        replay.Frames.Where(frame => frame.Views.Omniscient.Move is not null)
            .Select(frame => frame.Views.Omniscient.Move!.Ply)
            .Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Rejected_takeback_is_idempotent_and_the_same_move_cannot_be_requested_again()
    {
        using var first = await CreatePlayerAsync();
        using var second = await CreatePlayerAsync();
        var gameId = await CreateStartedRoomGameAsync(first, second);
        var turn = await GetCurrentTurnAsync(first, second, gameId);
        var moved = await PlayCandidateMoveAsync(gameId, turn, "rejected-window-move");
        var command = new CreateTakebackRequest(moved.Version, "rejected-window-request");
        var pending = await PostAsync<TakebackRequestView>(
            turn.Mover,
            $"/api/games/{gameId:D}/takeback-requests",
            command);

        var rejected = await PostAsync<TakebackRequestView>(
            turn.Opponent,
            $"/api/games/{gameId:D}/takeback-requests/{pending.Id:D}/reject",
            body: null);
        rejected.Id.Should().Be(pending.Id);
        rejected.Status.Should().Be(TakebackRequestStatus.Rejected);
        rejected.ResolvedAtVersion.Should().Be(moved.Version);
        rejected.Revision.Should().Be(pending.Revision + 1);

        var repeatedRejection = await PostAsync<TakebackRequestView>(
            turn.Opponent,
            $"/api/games/{gameId:D}/takeback-requests/{pending.Id:D}/reject",
            body: null);
        repeatedRejection.Should().BeEquivalentTo(rejected);
        var repeatedCreation = await PostAsync<TakebackRequestView>(
            turn.Mover,
            $"/api/games/{gameId:D}/takeback-requests",
            command);
        repeatedCreation.Should().BeEquivalentTo(rejected);

        using var replacement = await PostResponseAsync(
            turn.Mover,
            $"/api/games/{gameId:D}/takeback-requests",
            new CreateTakebackRequest(moved.Version, "rejected-window-replacement"));
        replacement.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadAsync<ErrorResponse>(replacement)).Code.Should().Be("TAKEBACK_ALREADY_REQUESTED");

        var moverView = await GetAsync<ApiGameView>(turn.Mover, $"/api/games/{gameId:D}");
        var opponentView = await GetAsync<ApiGameView>(turn.Opponent, $"/api/games/{gameId:D}");
        moverView.TakebackRequest.Should().BeNull();
        opponentView.TakebackRequest.Should().BeNull();
        moverView.NegotiationVersion.Should().Be(rejected.Revision);
        opponentView.NegotiationVersion.Should().Be(rejected.Revision);
        moverView.CanRequestTakeback.Should().BeFalse();

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var audit = new NpgsqlCommand(
            """
            SELECT request.status,
                   request.resolved_at_version,
                   move.reverted_at IS NULL,
                   game.takeback_window_consumed,
                   game.negotiation_version,
                   (SELECT count(*) FROM takeback_requests WHERE game_id = game.id)
            FROM takeback_requests AS request
            INNER JOIN games AS game ON game.id = request.game_id
            INNER JOIN moves AS move ON move.id = request.move_id
            WHERE request.id = @request_id AND request.game_id = @game_id
            """,
            connection);
        audit.Parameters.AddWithValue("request_id", pending.Id);
        audit.Parameters.AddWithValue("game_id", gameId);
        await using var reader = await audit.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be("Rejected");
        reader.GetInt64(1).Should().Be(moved.Version);
        reader.GetBoolean(2).Should().BeTrue();
        reader.GetBoolean(3).Should().BeTrue();
        reader.GetInt64(4).Should().Be(rejected.Revision);
        reader.GetInt64(5).Should().Be(1);
        (await reader.ReadAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Draw_offer_and_takeback_are_mutually_exclusive_in_both_directions()
    {
        using var first = await CreatePlayerAsync();
        using var second = await CreatePlayerAsync();
        var gameId = await CreateStartedRoomGameAsync(first, second);
        var turn = await GetCurrentTurnAsync(first, second, gameId);
        var moved = await PlayCandidateMoveAsync(gameId, turn, "mutual-exclusion-move");

        var draw = await PostAsync<DrawOfferView>(
            turn.Opponent,
            $"/api/games/{gameId:D}/draw-offers",
            body: null);
        draw.Id.Should().NotBeEmpty();
        draw.Revision.Should().Be(moved.NegotiationVersion + 1);
        using var takebackBlocked = await PostResponseAsync(
            turn.Mover,
            $"/api/games/{gameId:D}/takeback-requests",
            new CreateTakebackRequest(moved.Version, "draw-blocks-takeback"));
        takebackBlocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadAsync<ErrorResponse>(takebackBlocked)).Code.Should().Be("NEGOTIATION_PENDING");

        var rejectedDraw = await PostAsync<DrawOfferView>(
            turn.Mover,
            $"/api/games/{gameId:D}/draw-offers/reject",
            body: null);
        rejectedDraw.Id.Should().Be(draw.Id);
        rejectedDraw.Revision.Should().Be(draw.Revision + 1);
        var takeback = await PostAsync<TakebackRequestView>(
            turn.Mover,
            $"/api/games/{gameId:D}/takeback-requests",
            new CreateTakebackRequest(moved.Version, "takeback-blocks-draw"));
        takeback.Revision.Should().Be(rejectedDraw.Revision + 1);

        using var drawBlocked = await PostResponseAsync(
            turn.Opponent,
            $"/api/games/{gameId:D}/draw-offers",
            body: null);
        drawBlocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadAsync<ErrorResponse>(drawBlocked)).Code.Should().Be("NEGOTIATION_PENDING");

        var publicView = await GetAsync<ApiGameView>(turn.Opponent, $"/api/games/{gameId:D}");
        publicView.DrawOffer.Should().BeNull();
        publicView.TakebackRequest.Should().BeEquivalentTo(takeback);
        publicView.NegotiationVersion.Should().Be(takeback.Revision);

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var audit = new NpgsqlCommand(
            """
            SELECT game.negotiation_version,
                   (SELECT count(*) FROM draw_offers WHERE game_id = game.id AND status = 'Pending'),
                   (SELECT count(*) FROM draw_offers WHERE game_id = game.id AND status = 'Rejected'),
                   (SELECT count(*) FROM takeback_requests WHERE game_id = game.id AND status = 'Pending')
            FROM games AS game
            WHERE game.id = @game_id
            """,
            connection);
        audit.Parameters.AddWithValue("game_id", gameId);
        await using var reader = await audit.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt64(0).Should().Be(takeback.Revision);
        reader.GetInt64(1).Should().Be(0);
        reader.GetInt64(2).Should().Be(1);
        reader.GetInt64(3).Should().Be(1);
        (await reader.ReadAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Accepting_takeback_restores_board_turn_and_clocks_while_soft_reverted_moves_disappear_from_history()
    {
        using var first = await CreatePlayerAsync();
        using var second = await CreatePlayerAsync();
        var gameId = await CreateStartedRoomGameAsync(first, second, "600+5", 30);
        await using (var clockConnection = new NpgsqlConnection(database.ConnectionString))
        {
            await clockConnection.OpenAsync();
            await using var setClock = new NpgsqlCommand(
                """
                UPDATE games
                SET red_milliseconds = 52000,
                    black_milliseconds = 47000,
                    turn_started_at = now() - interval '4 seconds',
                    turn_milliseconds = 30000,
                    clock_expires_at = now() + interval '26 seconds'
                WHERE id = @game_id
                """,
                clockConnection);
            setClock.Parameters.AddWithValue("game_id", gameId);
            (await setClock.ExecuteNonQueryAsync()).Should().Be(1);
        }

        var initialFirst = await GetAsync<ApiGameView>(first, $"/api/games/{gameId:D}");
        var initialSecond = await GetAsync<ApiGameView>(second, $"/api/games/{gameId:D}");
        var turn = await GetCurrentTurnAsync(first, second, gameId);
        var moved = await PlayCandidateMoveAsync(gameId, turn, "restore-clock-move");
        var pending = await PostAsync<TakebackRequestView>(
            turn.Mover,
            $"/api/games/{gameId:D}/takeback-requests",
            new CreateTakebackRequest(moved.Version, "restore-clock-request"));

        var accepted = await PostAsync<ApiGameView>(
            turn.Opponent,
            $"/api/games/{gameId:D}/takeback-requests/{pending.Id:D}/accept",
            body: null);
        accepted.Version.Should().Be(moved.Version + 1);
        accepted.Status.Should().Be(GameStatus.Playing);
        accepted.SideToMove.Should().Be(turn.MoverView.SideToMove);
        accepted.LastAction.Should().Be(new GameActionView(
            accepted.Version,
            GameActionKind.TakebackAccepted,
            turn.MoverView.SideToMove));
        accepted.TakebackRequest.Should().BeNull();
        accepted.CanRequestTakeback.Should().BeFalse();

        var repeatedAcceptance = await PostAsync<ApiGameView>(
            turn.Opponent,
            $"/api/games/{gameId:D}/takeback-requests/{pending.Id:D}/accept",
            body: null);
        repeatedAcceptance.Version.Should().Be(accepted.Version);
        repeatedAcceptance.Status.Should().Be(GameStatus.Playing);
        repeatedAcceptance.SideToMove.Should().Be(accepted.SideToMove);
        repeatedAcceptance.Pieces.Should().BeEquivalentTo(accepted.Pieces);
        repeatedAcceptance.LastAction.Should().Be(accepted.LastAction);
        repeatedAcceptance.TakebackRequest.Should().BeNull();
        repeatedAcceptance.CanRequestTakeback.Should().BeFalse();

        var restoredFirst = await GetAsync<ApiGameView>(first, $"/api/games/{gameId:D}");
        var restoredSecond = await GetAsync<ApiGameView>(second, $"/api/games/{gameId:D}");
        restoredFirst.Version.Should().Be(accepted.Version);
        restoredSecond.Version.Should().Be(accepted.Version);
        restoredFirst.SideToMove.Should().Be(initialFirst.SideToMove);
        restoredSecond.SideToMove.Should().Be(initialSecond.SideToMove);
        restoredFirst.Pieces.Should().BeEquivalentTo(initialFirst.Pieces);
        restoredSecond.Pieces.Should().BeEquivalentTo(initialSecond.Pieces);
        restoredFirst.VisibleSquares.Should().Equal(initialFirst.VisibleSquares);
        restoredSecond.VisibleSquares.Should().Equal(initialSecond.VisibleSquares);
        restoredFirst.CaptureSummary.Should().BeEquivalentTo(initialFirst.CaptureSummary);
        restoredSecond.CaptureSummary.Should().BeEquivalentTo(initialSecond.CaptureSummary);
        restoredFirst.CandidateMoves.Should().BeEquivalentTo(initialFirst.CandidateMoves);
        restoredSecond.CandidateMoves.Should().BeEquivalentTo(initialSecond.CandidateMoves);

        long persistedRed;
        long persistedBlack;
        long persistedTurn;
        await using (var auditConnection = new NpgsqlConnection(database.ConnectionString))
        {
            await auditConnection.OpenAsync();
            await using var audit = new NpgsqlCommand(
                """
                SELECT game.version,
                       game.side_to_move,
                       game.red_milliseconds,
                       game.black_milliseconds,
                       game.turn_milliseconds,
                       game.last_action_version,
                       game.last_action_kind,
                       game.last_action_actor,
                       game.negotiation_version,
                       game.takeback_window_consumed,
                       move.elapsed_milliseconds,
                       move.turn_milliseconds_before,
                       move.red_milliseconds_after,
                       move.black_milliseconds_after,
                       move.reverted_at IS NOT NULL,
                       move.reverted_by_takeback_request_id,
                       request.status,
                       request.resolved_at_version,
                       game.state = game.initial_state,
                       (SELECT count(*) FROM moves WHERE game_id = game.id),
                       (SELECT count(*) FROM moves WHERE game_id = game.id AND reverted_at IS NULL)
                FROM games AS game
                INNER JOIN takeback_requests AS request ON request.game_id = game.id
                INNER JOIN moves AS move ON move.id = request.move_id
                WHERE game.id = @game_id AND request.id = @request_id
                """,
                auditConnection);
            audit.Parameters.AddWithValue("game_id", gameId);
            audit.Parameters.AddWithValue("request_id", pending.Id);
            await using var reader = await audit.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt64(0).Should().Be(accepted.Version);
            reader.GetString(1).Should().Be(turn.MoverView.SideToMove.ToString());
            persistedRed = reader.GetInt64(2);
            persistedBlack = reader.GetInt64(3);
            persistedTurn = reader.GetInt64(4);
            reader.GetInt64(5).Should().Be(accepted.Version);
            reader.GetString(6).Should().Be("takebackAccepted");
            reader.GetString(7).Should().Be(turn.MoverView.SideToMove.ToString());
            reader.GetInt64(8).Should().Be(accepted.NegotiationVersion);
            reader.GetBoolean(9).Should().BeTrue();
            var elapsed = reader.GetInt64(10);
            var turnBefore = reader.GetInt64(11);
            var redAfterMove = reader.GetInt64(12);
            var blackAfterMove = reader.GetInt64(13);
            persistedTurn.Should().Be(Math.Max(0, turnBefore - elapsed));
            if (turn.MoverView.SideToMove == Side.Red)
            {
                persistedRed.Should().Be(redAfterMove - 5_000);
                persistedBlack.Should().BeLessThanOrEqualTo(blackAfterMove);
            }
            else
            {
                persistedBlack.Should().Be(blackAfterMove - 5_000);
                persistedRed.Should().BeLessThanOrEqualTo(redAfterMove);
            }
            reader.GetBoolean(14).Should().BeTrue();
            reader.GetGuid(15).Should().Be(pending.Id);
            reader.GetString(16).Should().Be("Accepted");
            reader.GetInt64(17).Should().Be(accepted.Version);
            reader.GetBoolean(18).Should().BeTrue();
            reader.GetInt64(19).Should().Be(1);
            reader.GetInt64(20).Should().Be(0);
            (await reader.ReadAsync()).Should().BeFalse();
        }

        accepted.Clock.Should().NotBeNull();
        var persistedCurrentTotal = accepted.SideToMove == Side.Red ? persistedRed : persistedBlack;
        var projectedCurrentTotal = accepted.SideToMove == Side.Red
            ? accepted.Clock!.RedMilliseconds
            : accepted.Clock!.BlackMilliseconds;
        projectedCurrentTotal.Should().BeInRange(Math.Max(0, persistedCurrentTotal - 5_000), persistedCurrentTotal);
        accepted.Clock!.TurnMilliseconds.Should().NotBeNull();
        accepted.Clock.TurnMilliseconds!.Value.Should().BeInRange(
            Math.Max(0, persistedTurn - 5_000),
            persistedTurn);
        (accepted.SideToMove == Side.Red
                ? accepted.Clock.BlackMilliseconds
                : accepted.Clock.RedMilliseconds)
            .Should().Be(accepted.SideToMove == Side.Red ? persistedBlack : persistedRed);

        await PostAsync<ApiGameView>(first, $"/api/games/{gameId:D}/resign", body: null);
        var history = await GetAsync<HistoricalGamesPageView>(first, "/api/games/history?limit=10");
        history.Games.Should().ContainSingle(game => game.GameId == gameId && game.PlyCount == 0);
        var replay = await GetAsync<HistoricalReplayView>(first, $"/api/games/{gameId:D}/replay");
        replay.Frames.Should().HaveCount(2);
        replay.Frames.Should().OnlyContain(frame => frame.Views.Omniscient.Move == null);
    }

    [Fact]
    public async Task Opponent_move_withdraws_pending_takeback_and_exposes_only_the_new_effective_move()
    {
        using var first = await CreatePlayerAsync();
        using var second = await CreatePlayerAsync();
        var gameId = await CreateStartedRoomGameAsync(first, second);
        var firstTurn = await GetCurrentTurnAsync(first, second, gameId);
        var firstMove = await PlayCandidateMoveAsync(gameId, firstTurn, "withdrawn-first-move");
        var pending = await PostAsync<TakebackRequestView>(
            firstTurn.Mover,
            $"/api/games/{gameId:D}/takeback-requests",
            new CreateTakebackRequest(firstMove.Version, "withdrawn-request"));

        var secondTurn = await GetCurrentTurnAsync(first, second, gameId);
        secondTurn.Mover.Should().BeSameAs(firstTurn.Opponent);
        var secondMove = await PlayCandidateMoveAsync(gameId, secondTurn, "withdrawn-second-move");
        secondMove.Version.Should().Be(firstMove.Version + 1);
        secondMove.NegotiationVersion.Should().Be(pending.Revision + 1);
        secondMove.TakebackRequest.Should().BeNull();
        secondMove.CanRequestTakeback.Should().BeTrue();
        secondMove.LastAction.Should().Be(new GameActionView(
            secondMove.Version,
            GameActionKind.Move,
            secondTurn.MoverView.SideToMove));

        var requesterView = await GetAsync<ApiGameView>(firstTurn.Mover, $"/api/games/{gameId:D}");
        var opponentView = await GetAsync<ApiGameView>(firstTurn.Opponent, $"/api/games/{gameId:D}");
        requesterView.TakebackRequest.Should().BeNull();
        opponentView.TakebackRequest.Should().BeNull();
        requesterView.CanRequestTakeback.Should().BeFalse();
        opponentView.CanRequestTakeback.Should().BeTrue();
        requesterView.LastAction.Should().Be(secondMove.LastAction);
        opponentView.LastAction.Should().Be(secondMove.LastAction);

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var audit = new NpgsqlCommand(
            """
            SELECT request.status,
                   request.resolved_at_version,
                   game.version,
                   game.negotiation_version,
                   game.takeback_window_consumed,
                   game.last_action_version,
                   game.last_action_kind,
                   game.last_action_actor,
                   (SELECT count(*) FROM moves WHERE game_id = game.id AND reverted_at IS NULL)
            FROM takeback_requests AS request
            INNER JOIN games AS game ON game.id = request.game_id
            WHERE request.id = @request_id AND request.game_id = @game_id
            """,
            connection);
        audit.Parameters.AddWithValue("request_id", pending.Id);
        audit.Parameters.AddWithValue("game_id", gameId);
        await using var reader = await audit.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be("Withdrawn");
        reader.GetInt64(1).Should().Be(secondMove.Version);
        reader.GetInt64(2).Should().Be(secondMove.Version);
        reader.GetInt64(3).Should().Be(secondMove.NegotiationVersion);
        reader.GetBoolean(4).Should().BeFalse();
        reader.GetInt64(5).Should().Be(secondMove.Version);
        reader.GetString(6).Should().Be("move");
        reader.GetString(7).Should().Be(secondTurn.MoverView.SideToMove.ToString());
        reader.GetInt64(8).Should().Be(2);
        (await reader.ReadAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Concurrent_takeback_accept_and_opponent_move_commit_one_authoritative_transition()
    {
        using var first = await CreatePlayerAsync();
        using var second = await CreatePlayerAsync();
        var gameId = await CreateStartedRoomGameAsync(first, second);
        var firstTurn = await GetCurrentTurnAsync(first, second, gameId);
        var firstMove = await PlayCandidateMoveAsync(gameId, firstTurn, "accept-move-race-first");
        var pending = await PostAsync<TakebackRequestView>(
            firstTurn.Mover,
            $"/api/games/{gameId:D}/takeback-requests",
            new CreateTakebackRequest(firstMove.Version, "accept-move-race-request"));
        var secondTurn = await GetCurrentTurnAsync(first, second, gameId);
        var candidate = secondTurn.MoverView.CandidateMoves.First(move => move.Destinations.Count > 0);
        var moveRequest = new MoveRequest(
            candidate.From,
            candidate.Destinations[0],
            firstMove.Version,
            "accept-move-race-second");

        var responses = await Task.WhenAll(
            PostResponseAsync(
                firstTurn.Opponent,
                $"/api/games/{gameId:D}/takeback-requests/{pending.Id:D}/accept",
                body: null),
            PostResponseAsync(
                secondTurn.Mover,
                $"/api/games/{gameId:D}/moves",
                moveRequest));
        using var acceptResponse = responses[0];
        using var moveResponse = responses[1];
        responses.Should().ContainSingle(response => response.StatusCode == HttpStatusCode.OK);
        responses.Should().ContainSingle(response => response.StatusCode == HttpStatusCode.Conflict);
        if (acceptResponse.StatusCode == HttpStatusCode.OK)
        {
            (await ReadAsync<ErrorResponse>(moveResponse)).Code.Should().Be("STALE_VERSION");
        }
        else
        {
            (await ReadAsync<ErrorResponse>(acceptResponse)).Code.Should().Be("TAKEBACK_WINDOW_CLOSED");
        }

        var requesterView = await GetAsync<ApiGameView>(firstTurn.Mover, $"/api/games/{gameId:D}");
        var responderView = await GetAsync<ApiGameView>(firstTurn.Opponent, $"/api/games/{gameId:D}");
        requesterView.Status.Should().Be(GameStatus.Playing);
        responderView.Status.Should().Be(GameStatus.Playing);
        requesterView.Version.Should().Be(firstMove.Version + 1);
        responderView.Version.Should().Be(requesterView.Version);
        requesterView.TakebackRequest.Should().BeNull();
        responderView.TakebackRequest.Should().BeNull();
        requesterView.LastAction.Should().Be(responderView.LastAction);
        requesterView.NegotiationVersion.Should().Be(pending.Revision + 1);

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var audit = new NpgsqlCommand(
            """
            SELECT request.status,
                   request.resolved_at_version,
                   game.version,
                   game.status,
                   game.last_action_version,
                   game.last_action_kind,
                   game.last_action_actor,
                   game.negotiation_version,
                   (SELECT count(*) FROM moves WHERE game_id = game.id AND reverted_at IS NULL),
                   (SELECT count(*) FROM moves WHERE game_id = game.id AND reverted_at IS NOT NULL)
            FROM takeback_requests AS request
            INNER JOIN games AS game ON game.id = request.game_id
            WHERE request.id = @request_id AND request.game_id = @game_id
            """,
            connection);
        audit.Parameters.AddWithValue("request_id", pending.Id);
        audit.Parameters.AddWithValue("game_id", gameId);
        await using var reader = await audit.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        var requestStatus = reader.GetString(0);
        requestStatus.Should().BeOneOf("Accepted", "Withdrawn");
        reader.GetInt64(1).Should().Be(requesterView.Version);
        reader.GetInt64(2).Should().Be(requesterView.Version);
        reader.GetString(3).Should().Be(GameStatus.Playing.ToString());
        reader.GetInt64(7).Should().Be(requesterView.NegotiationVersion);
        if (requestStatus == "Accepted")
        {
            acceptResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            reader.GetInt64(4).Should().Be(requesterView.Version);
            reader.GetString(5).Should().Be("takebackAccepted");
            reader.GetString(6).Should().Be(firstTurn.MoverView.SideToMove.ToString());
            reader.GetInt64(8).Should().Be(0);
            reader.GetInt64(9).Should().Be(1);
            requesterView.LastAction.Should().Be(new GameActionView(
                requesterView.Version,
                GameActionKind.TakebackAccepted,
                firstTurn.MoverView.SideToMove));
            requesterView.CanRequestTakeback.Should().BeFalse();
        }
        else
        {
            moveResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            reader.GetInt64(4).Should().Be(requesterView.Version);
            reader.GetString(5).Should().Be("move");
            reader.GetString(6).Should().Be(secondTurn.MoverView.SideToMove.ToString());
            reader.GetInt64(8).Should().Be(2);
            reader.GetInt64(9).Should().Be(0);
            requesterView.LastAction.Should().Be(new GameActionView(
                requesterView.Version,
                GameActionKind.Move,
                secondTurn.MoverView.SideToMove));
            responderView.CanRequestTakeback.Should().BeTrue();
        }
        (await reader.ReadAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Expired_clock_wins_concurrently_over_takeback_and_preserves_the_effective_move()
    {
        using var first = await CreatePlayerAsync();
        using var second = await CreatePlayerAsync();
        var gameId = await CreateStartedRoomGameAsync(first, second, "60+1");
        var turn = await GetCurrentTurnAsync(first, second, gameId);
        var moved = await PlayCandidateMoveAsync(gameId, turn, "timeout-takeback-move");
        var pending = await PostAsync<TakebackRequestView>(
            turn.Mover,
            $"/api/games/{gameId:D}/takeback-requests",
            new CreateTakebackRequest(moved.Version, "timeout-takeback-request"));
        var timedOutSide = moved.SideToMove;
        var expectedWinner = timedOutSide == Side.Red ? Side.Black : Side.Red;
        await ExpireCurrentTurnAsync(gameId);

        var responses = await Task.WhenAll(
            PostResponseAsync(
                turn.Opponent,
                $"/api/games/{gameId:D}/takeback-requests/{pending.Id:D}/accept",
                body: null),
            PostResponseAsync(turn.Opponent, $"/api/games/{gameId:D}/resign", body: null));
        using var acceptResponse = responses[0];
        using var resignResponse = responses[1];
        acceptResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadAsync<ErrorResponse>(acceptResponse)).Code.Should().Be("TAKEBACK_WINDOW_CLOSED");
        resignResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var authoritative = await GetAsync<ApiGameView>(turn.Mover, $"/api/games/{gameId:D}");
        authoritative.Status.Should().Be(GameStatus.Finished);
        authoritative.Result.Should().Be(new GameResultView(expectedWinner, GameResultReason.Timeout));
        authoritative.Version.Should().Be(moved.Version + 1);
        authoritative.TakebackRequest.Should().BeNull();
        authoritative.LastAction.Should().Be(moved.LastAction);

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var audit = new NpgsqlCommand(
            """
            SELECT request.status,
                   request.resolved_at_version,
                   game.version,
                   game.status,
                   game.winner,
                   game.result_reason,
                   game.negotiation_version,
                   move.reverted_at IS NULL,
                   (SELECT count(*) FROM moves WHERE game_id = game.id AND reverted_at IS NULL)
            FROM takeback_requests AS request
            INNER JOIN games AS game ON game.id = request.game_id
            INNER JOIN moves AS move ON move.id = request.move_id
            WHERE request.id = @request_id AND request.game_id = @game_id
            """,
            connection);
        audit.Parameters.AddWithValue("request_id", pending.Id);
        audit.Parameters.AddWithValue("game_id", gameId);
        await using var reader = await audit.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be("Withdrawn");
        reader.GetInt64(1).Should().Be(authoritative.Version);
        reader.GetInt64(2).Should().Be(authoritative.Version);
        reader.GetString(3).Should().Be(GameStatus.Finished.ToString());
        reader.GetString(4).Should().Be(expectedWinner.ToString());
        reader.GetString(5).Should().Be(GameResultReason.Timeout.ToString());
        reader.GetInt64(6).Should().Be(authoritative.NegotiationVersion);
        reader.GetBoolean(7).Should().BeTrue();
        reader.GetInt64(8).Should().Be(1);
        (await reader.ReadAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Concurrent_takeback_accept_and_resignation_leave_one_self_consistent_terminal_audit()
    {
        using var first = await CreatePlayerAsync();
        using var second = await CreatePlayerAsync();
        var gameId = await CreateStartedRoomGameAsync(first, second);
        var turn = await GetCurrentTurnAsync(first, second, gameId);
        var moved = await PlayCandidateMoveAsync(gameId, turn, "resign-takeback-move");
        var pending = await PostAsync<TakebackRequestView>(
            turn.Mover,
            $"/api/games/{gameId:D}/takeback-requests",
            new CreateTakebackRequest(moved.Version, "resign-takeback-request"));
        var expectedWinner = turn.MoverView.SideToMove;

        await using var lockConnection = new NpgsqlConnection(database.ConnectionString);
        await lockConnection.OpenAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand(
            "SELECT 1 FROM games WHERE id = @game_id FOR UPDATE",
            lockConnection,
            lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("game_id", gameId);
            (await lockCommand.ExecuteScalarAsync()).Should().Be(1);
        }

        var acceptTask = PostResponseAsync(
            turn.Opponent,
            $"/api/games/{gameId:D}/takeback-requests/{pending.Id:D}/accept",
            body: null);
        await using var monitorConnection = new NpgsqlConnection(database.ConnectionString);
        await monitorConnection.OpenAsync();
        await using var blockingCommand = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM pg_stat_activity
            WHERE datname = current_database()
              AND pid <> pg_backend_pid()
              AND cardinality(pg_blocking_pids(pid)) > 0
            """,
            monitorConnection);
        var blockedRequestCount = 0;
        for (var attempt = 0; attempt < 200 && blockedRequestCount < 1; attempt++)
        {
            blockedRequestCount = Convert.ToInt32(await blockingCommand.ExecuteScalarAsync());
            if (blockedRequestCount < 1)
            {
                await Task.Delay(25);
            }
        }

        var resignTask = PostResponseAsync(turn.Opponent, $"/api/games/{gameId:D}/resign", body: null);
        for (var attempt = 0; attempt < 200 && blockedRequestCount < 2; attempt++)
        {
            blockedRequestCount = Convert.ToInt32(await blockingCommand.ExecuteScalarAsync());
            if (blockedRequestCount < 2)
            {
                await Task.Delay(25);
            }
        }

        await lockTransaction.CommitAsync();
        var responses = await Task.WhenAll(acceptTask, resignTask);
        blockedRequestCount.Should().BeGreaterThanOrEqualTo(
            2,
            "both commands must wait on the same game row before the lock is released");
        using var acceptResponse = responses[0];
        using var resignResponse = responses[1];
        resignResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        acceptResponse.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Conflict);
        if (acceptResponse.StatusCode == HttpStatusCode.Conflict)
        {
            (await ReadAsync<ErrorResponse>(acceptResponse)).Code.Should().Be("TAKEBACK_WINDOW_CLOSED");
        }

        var authoritative = await GetAsync<ApiGameView>(turn.Mover, $"/api/games/{gameId:D}");
        authoritative.Status.Should().Be(GameStatus.Finished);
        authoritative.Result.Should().Be(new GameResultView(expectedWinner, GameResultReason.Resignation));
        authoritative.TakebackRequest.Should().BeNull();

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var audit = new NpgsqlCommand(
            """
            SELECT request.status,
                   request.resolved_at_version,
                   game.version,
                   game.winner,
                   game.result_reason,
                   game.last_action_version,
                   game.last_action_kind,
                   game.last_action_actor,
                   game.negotiation_version,
                   move.reverted_at IS NOT NULL,
                   (SELECT count(*) FROM moves WHERE game_id = game.id AND reverted_at IS NULL)
            FROM takeback_requests AS request
            INNER JOIN games AS game ON game.id = request.game_id
            INNER JOIN moves AS move ON move.id = request.move_id
            WHERE request.id = @request_id AND request.game_id = @game_id
            """,
            connection);
        audit.Parameters.AddWithValue("request_id", pending.Id);
        audit.Parameters.AddWithValue("game_id", gameId);
        await using var reader = await audit.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        var requestStatus = reader.GetString(0);
        requestStatus.Should().BeOneOf("Accepted", "Withdrawn");
        reader.GetInt64(2).Should().Be(authoritative.Version);
        reader.GetString(3).Should().Be(expectedWinner.ToString());
        reader.GetString(4).Should().Be(GameResultReason.Resignation.ToString());
        reader.GetInt64(8).Should().Be(authoritative.NegotiationVersion);
        if (requestStatus == "Accepted")
        {
            acceptResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            authoritative.Version.Should().Be(moved.Version + 2);
            reader.GetInt64(1).Should().Be(moved.Version + 1);
            reader.GetInt64(5).Should().Be(moved.Version + 1);
            reader.GetString(6).Should().Be("takebackAccepted");
            reader.GetString(7).Should().Be(turn.MoverView.SideToMove.ToString());
            reader.GetBoolean(9).Should().BeTrue();
            reader.GetInt64(10).Should().Be(0);
            authoritative.LastAction.Should().Be(new GameActionView(
                moved.Version + 1,
                GameActionKind.TakebackAccepted,
                turn.MoverView.SideToMove));
        }
        else
        {
            acceptResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
            authoritative.Version.Should().Be(moved.Version + 1);
            reader.GetInt64(1).Should().Be(authoritative.Version);
            reader.GetInt64(5).Should().Be(moved.Version);
            reader.GetString(6).Should().Be("move");
            reader.GetString(7).Should().Be(turn.MoverView.SideToMove.ToString());
            reader.GetBoolean(9).Should().BeFalse();
            reader.GetInt64(10).Should().Be(1);
            authoritative.LastAction.Should().Be(moved.LastAction);
        }
        (await reader.ReadAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Timed_room_enforces_per_move_limit_without_zeroing_total_clock()
    {
        using var first = await CreatePlayerAsync();
        using var second = await CreatePlayerAsync();
        var room = await PostAsync<RoomView>(
            first,
            "/api/rooms",
            new CreateRoomRequest(GameState.CurrentRuleVersion, "600+5", 90));
        room.MoveTimeLimitSeconds.Should().Be(90);
        await PostAsync<RoomView>(second, $"/api/rooms/{room.Code}/join", body: null);
        await PostAsync<RoomView>(first, $"/api/rooms/{room.Code}/ready", new SetReadyRequest(true));
        var started = await PostAsync<RoomView>(
            second,
            $"/api/rooms/{room.Code}/ready",
            new SetReadyRequest(true));
        var gameId = started.GameId!.Value;
        var before = await GetAsync<ApiGameView>(first, $"/api/games/{gameId:D}");
        before.MoveTimeLimitSeconds.Should().Be(90);
        before.Clock!.TurnMilliseconds.Should().BeInRange(89_000, 90_000);
        var timedOutSide = before.SideToMove;

        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                """
                UPDATE games
                SET turn_started_at = now() - interval '90 seconds',
                    turn_milliseconds = 90000,
                    clock_expires_at = now() - interval '1 millisecond'
                WHERE id = @id
                """,
                connection);
            command.Parameters.AddWithValue("id", gameId);
            (await command.ExecuteNonQueryAsync()).Should().Be(1);
        }

        var finished = await PostAsync<ApiGameView>(first, $"/api/games/{gameId:D}/resign", body: null);
        finished.Status.Should().Be(GameStatus.Finished);
        finished.Result.Should().Be(new GameResultView(
            timedOutSide == Side.Red ? Side.Black : Side.Red,
            GameResultReason.Timeout));
        finished.Clock!.TurnMilliseconds.Should().Be(0);
        var totalRemaining = timedOutSide == Side.Red
            ? finished.Clock.RedMilliseconds
            : finished.Clock.BlackMilliseconds;
        totalRemaining.Should().BeInRange(509_000, 510_000);
    }

    [Fact]
    public async Task Accepting_draw_after_current_clock_expires_finishes_by_timeout()
    {
        using var first = await CreatePlayerAsync();
        using var second = await CreatePlayerAsync();
        var room = await PostAsync<RoomView>(
            first,
            "/api/rooms",
            new CreateRoomRequest(GameState.CurrentRuleVersion, "60+1"));
        await PostAsync<RoomView>(second, $"/api/rooms/{room.Code}/join", body: null);
        await PostAsync<RoomView>(first, $"/api/rooms/{room.Code}/ready", new SetReadyRequest(true));
        var started = await PostAsync<RoomView>(
            second,
            $"/api/rooms/{room.Code}/ready",
            new SetReadyRequest(true));
        var gameId = started.GameId!.Value;
        var before = await GetAsync<ApiGameView>(first, $"/api/games/{gameId:D}");
        var timedOutSide = before.SideToMove;
        var expectedWinner = timedOutSide == Side.Red ? Side.Black : Side.Red;

        await PostAsync<DrawOfferView>(first, $"/api/games/{gameId:D}/draw-offers", body: null);
        await ExpireCurrentTurnAsync(gameId);

        var accepted = await PostAsync<ApiGameView>(
            second,
            $"/api/games/{gameId:D}/draw-offers/accept",
            body: null);
        accepted.Status.Should().Be(GameStatus.Finished);
        accepted.Result.Should().Be(new GameResultView(expectedWinner, GameResultReason.Timeout));
        accepted.Clock.Should().NotBeNull();
        (timedOutSide == Side.Red
                ? accepted.Clock!.RedMilliseconds
                : accepted.Clock!.BlackMilliseconds)
            .Should().Be(0);

        using var repeated = await PostResponseAsync(
            second,
            $"/api/games/{gameId:D}/draw-offers/accept",
            body: null);
        repeated.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadAsync<ErrorResponse>(repeated)).Code.Should().Be("GAME_FINISHED");
        await AssertTimeoutPersistedAsync(gameId, expectedWinner, timedOutSide);
    }

    [Fact]
    public async Task Resigning_after_current_clock_expires_finishes_by_timeout()
    {
        using var first = await CreatePlayerAsync();
        using var second = await CreatePlayerAsync();
        var room = await PostAsync<RoomView>(
            first,
            "/api/rooms",
            new CreateRoomRequest(GameState.CurrentRuleVersion, "60+1"));
        await PostAsync<RoomView>(second, $"/api/rooms/{room.Code}/join", body: null);
        await PostAsync<RoomView>(first, $"/api/rooms/{room.Code}/ready", new SetReadyRequest(true));
        var started = await PostAsync<RoomView>(
            second,
            $"/api/rooms/{room.Code}/ready",
            new SetReadyRequest(true));
        var gameId = started.GameId!.Value;
        var before = await GetAsync<ApiGameView>(first, $"/api/games/{gameId:D}");
        var timedOutSide = before.SideToMove;
        var expectedWinner = timedOutSide == Side.Red ? Side.Black : Side.Red;
        var resigningClient = before.Perspective == timedOutSide ? second : first;

        await ExpireCurrentTurnAsync(gameId);

        var resigned = await PostAsync<ApiGameView>(
            resigningClient,
            $"/api/games/{gameId:D}/resign",
            body: null);
        resigned.Status.Should().Be(GameStatus.Finished);
        resigned.Result.Should().Be(new GameResultView(expectedWinner, GameResultReason.Timeout));
        resigned.Clock.Should().NotBeNull();
        (timedOutSide == Side.Red
                ? resigned.Clock!.RedMilliseconds
                : resigned.Clock!.BlackMilliseconds)
            .Should().Be(0);

        var repeated = await PostAsync<ApiGameView>(
            resigningClient,
            $"/api/games/{gameId:D}/resign",
            body: null);
        repeated.Version.Should().Be(resigned.Version);
        repeated.Result.Should().Be(new GameResultView(expectedWinner, GameResultReason.Timeout));
        await AssertTimeoutPersistedAsync(gameId, expectedWinner, timedOutSide);
    }

    [Fact]
    public async Task Move_that_settles_timeout_is_idempotent_without_adding_a_replay_ply()
    {
        using var first = await CreatePlayerAsync();
        using var second = await CreatePlayerAsync();
        var room = await PostAsync<RoomView>(
            first,
            "/api/rooms",
            new CreateRoomRequest(GameState.CurrentRuleVersion, "60+1"));
        await PostAsync<RoomView>(second, $"/api/rooms/{room.Code}/join", body: null);
        await PostAsync<RoomView>(first, $"/api/rooms/{room.Code}/ready", new SetReadyRequest(true));
        var started = await PostAsync<RoomView>(
            second,
            $"/api/rooms/{room.Code}/ready",
            new SetReadyRequest(true));
        var gameId = started.GameId!.Value;

        var firstView = await GetAsync<ApiGameView>(first, $"/api/games/{gameId:D}");
        var secondView = await GetAsync<ApiGameView>(second, $"/api/games/{gameId:D}");
        var movingClient = firstView.Perspective == firstView.SideToMove ? first : second;
        var movingView = firstView.Perspective == firstView.SideToMove ? firstView : secondView;
        var candidate = movingView.CandidateMoves.First(value => value.Destinations.Count > 0);
        var request = new MoveRequest(
            candidate.From,
            candidate.Destinations[0],
            movingView.Version,
            "timeout-command");
        var timedOutSide = movingView.SideToMove;
        var expectedWinner = timedOutSide == Side.Red ? Side.Black : Side.Red;

        await ExpireCurrentTurnAsync(gameId);

        var firstResult = await PostAsync<ApiGameView>(
            movingClient,
            $"/api/games/{gameId:D}/moves",
            request);
        var repeatedResult = await PostAsync<ApiGameView>(
            movingClient,
            $"/api/games/{gameId:D}/moves",
            request);

        firstResult.Result.Should().Be(new GameResultView(expectedWinner, GameResultReason.Timeout));
        repeatedResult.Should().BeEquivalentTo(firstResult);
        var replay = await GetAsync<HistoricalReplayView>(movingClient, $"/api/games/{gameId:D}/replay");
        replay.Frames.Should().HaveCount(2);
        replay.Frames.Should().OnlyContain(frame => frame.Views.Omniscient.Move == null);

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                (SELECT count(*) FROM move_command_receipts
                 WHERE game_id = @game_id AND client_move_id = @client_move_id),
                (SELECT count(*) FROM moves
                 WHERE game_id = @game_id AND client_move_id = @client_move_id)
            """,
            connection);
        command.Parameters.AddWithValue("game_id", gameId);
        command.Parameters.AddWithValue("client_move_id", request.ClientMoveId);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt64(0).Should().Be(1);
        reader.GetInt64(1).Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_moves_commit_once_and_same_command_id_is_idempotent()
    {
        using var first = await CreatePlayerAsync();
        using var second = await CreatePlayerAsync();
        var room = await PostAsync<RoomView>(
            first,
            "/api/rooms",
            new CreateRoomRequest(GameState.CurrentRuleVersion, null));
        await PostAsync<RoomView>(second, $"/api/rooms/{room.Code}/join", body: null);
        await PostAsync<RoomView>(first, $"/api/rooms/{room.Code}/ready", new SetReadyRequest(true));
        var started = await PostAsync<RoomView>(
            second,
            $"/api/rooms/{room.Code}/ready",
            new SetReadyRequest(true));
        var gameId = started.GameId!.Value;

        var firstView = await GetAsync<ApiGameView>(first, $"/api/games/{gameId:D}");
        var secondView = await GetAsync<ApiGameView>(second, $"/api/games/{gameId:D}");
        var currentClient = firstView.Perspective == firstView.SideToMove ? first : second;
        var currentView = firstView.Perspective == firstView.SideToMove ? firstView : secondView;
        var candidate = currentView.CandidateMoves.First(value => value.Destinations.Count > 0);
        var request = new MoveRequest(candidate.From, candidate.Destinations[0], currentView.Version, "race-a");

        var competing = await Task.WhenAll(
            PostResponseAsync(currentClient, $"/api/games/{gameId:D}/moves", request),
            PostResponseAsync(
                currentClient,
                $"/api/games/{gameId:D}/moves",
                request with { ClientMoveId = "race-b" }));
        competing.Should().ContainSingle(value => value.StatusCode == HttpStatusCode.OK);
        var stale = competing.Single(value => value.StatusCode == HttpStatusCode.Conflict);
        (await ReadAsync<ErrorResponse>(stale)).Code.Should().Be("STALE_VERSION");

        var afterRace = await GetAsync<ApiGameView>(first, $"/api/games/{gameId:D}");
        afterRace.Version.Should().Be(currentView.Version + 1);
        var nextClient = afterRace.Perspective == afterRace.SideToMove ? first : second;
        var nextView = afterRace.Perspective == afterRace.SideToMove
            ? afterRace
            : await GetAsync<ApiGameView>(second, $"/api/games/{gameId:D}");
        var nextCandidate = nextView.CandidateMoves.First(value => value.Destinations.Count > 0);
        var repeatedRequest = new MoveRequest(
            nextCandidate.From,
            nextCandidate.Destinations[0],
            nextView.Version,
            "same-command");

        var repeated = await Task.WhenAll(
            PostResponseAsync(nextClient, $"/api/games/{gameId:D}/moves", repeatedRequest),
            PostResponseAsync(nextClient, $"/api/games/{gameId:D}/moves", repeatedRequest));
        repeated.Should().OnlyContain(value => value.StatusCode == HttpStatusCode.OK);
        var repeatedViews = await Task.WhenAll(repeated.Select(ReadAsync<ApiGameView>));
        repeatedViews.Should().OnlyContain(value => value.Version == nextView.Version + 1);
        repeatedViews[1].Should().BeEquivalentTo(repeatedViews[0]);
        (await GetAsync<ApiGameView>(first, $"/api/games/{gameId:D}"))
            .Version.Should().Be(nextView.Version + 1);
    }

    [Fact]
    public async Task Ticket_cancellation_racing_with_matching_has_one_consistent_outcome()
    {
        using var first = await CreatePlayerAsync();
        using var second = await CreatePlayerAsync();
        var firstTicket = await PostAsync<MatchTicketView>(
            first,
            "/api/matchmaking/tickets",
            Ticket("cancel-race-a"));

        var competing = await Task.WhenAll(
            first.DeleteAsync($"/api/matchmaking/tickets/{firstTicket.TicketId:D}"),
            PostResponseAsync(
                second,
                "/api/matchmaking/tickets",
                Ticket("cancel-race-b")));
        var cancellation = competing[0];
        var creation = competing[1];
        creation.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondTicket = await ReadAsync<MatchTicketView>(creation);

        if (cancellation.StatusCode == HttpStatusCode.OK)
        {
            (await ReadAsync<MatchTicketView>(cancellation)).Status.Should().Be(MatchTicketStatus.Cancelled);
            secondTicket.Status.Should().Be(MatchTicketStatus.Searching);
        }
        else
        {
            cancellation.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await ReadAsync<ErrorResponse>(cancellation)).Code.Should().Be("MATCH_ALREADY_CREATED");
            secondTicket.Status.Should().Be(MatchTicketStatus.Matched);
            secondTicket.GameId.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task Guest_lock_contention_does_not_cancel_healthy_matchmaking_tickets()
    {
        using var first = await CreatePlayerAsync();
        using var second = await CreatePlayerAsync();
        var firstSession = await PostAsync<GuestSessionView>(first, "/api/sessions/guest", body: null);
        var firstTicket = await PostAsync<MatchTicketView>(
            first,
            "/api/matchmaking/tickets",
            Ticket("guest-lock-first"));

        await using var lockConnection = new NpgsqlConnection(database.ConnectionString);
        await lockConnection.OpenAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand(
            "SELECT 1 FROM guest_sessions WHERE id = @player_id FOR UPDATE",
            lockConnection,
            lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("player_id", firstSession.PlayerId);
            (await lockCommand.ExecuteScalarAsync()).Should().Be(1);
        }

        var secondTicket = await PostAsync<MatchTicketView>(
            second,
            "/api/matchmaking/tickets",
            Ticket("guest-lock-second"));
        secondTicket.Status.Should().Be(MatchTicketStatus.Searching);
        (await GetAsync<MatchTicketView>(
            first,
            "/api/matchmaking/tickets/current")).Status.Should().Be(MatchTicketStatus.Searching);

        await lockTransaction.CommitAsync();
        await _factory.Services
            .GetRequiredService<MatchmakingCoordinator>()
            .TryMatchAsync(CancellationToken.None);

        var matchedFirst = await GetAsync<MatchTicketView>(
            first,
            "/api/matchmaking/tickets/current");
        var matchedSecond = await GetAsync<MatchTicketView>(
            second,
            "/api/matchmaking/tickets/current");
        matchedFirst.Status.Should().Be(MatchTicketStatus.Matched);
        matchedSecond.Status.Should().Be(MatchTicketStatus.Matched);
        matchedSecond.GameId.Should().Be(matchedFirst.GameId);
        matchedFirst.TicketId.Should().Be(firstTicket.TicketId);
    }

    [Fact]
    public async Task Busy_oldest_guest_does_not_block_other_healthy_matchmaking_tickets()
    {
        using var first = await CreatePlayerAsync();
        using var second = await CreatePlayerAsync();
        using var third = await CreatePlayerAsync();
        var firstSession = await PostAsync<GuestSessionView>(
            first,
            "/api/sessions/guest",
            body: null);
        var firstTicket = await PostAsync<MatchTicketView>(
            first,
            "/api/matchmaking/tickets",
            Ticket("guest-lock-pool-first"));

        await using var lockConnection = new NpgsqlConnection(database.ConnectionString);
        await lockConnection.OpenAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand(
            "SELECT 1 FROM guest_sessions WHERE id = @player_id FOR UPDATE",
            lockConnection,
            lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("player_id", firstSession.PlayerId);
            (await lockCommand.ExecuteScalarAsync()).Should().Be(1);
        }

        var secondTicket = await PostAsync<MatchTicketView>(
            second,
            "/api/matchmaking/tickets",
            Ticket("guest-lock-pool-second"));
        secondTicket.Status.Should().Be(MatchTicketStatus.Searching);
        var thirdTicket = await PostAsync<MatchTicketView>(
            third,
            "/api/matchmaking/tickets",
            Ticket("guest-lock-pool-third"));

        thirdTicket.Status.Should().Be(MatchTicketStatus.Matched);
        thirdTicket.GameId.Should().NotBeNull();
        var matchedSecond = await GetAsync<MatchTicketView>(
            second,
            "/api/matchmaking/tickets/current");
        matchedSecond.Status.Should().Be(MatchTicketStatus.Matched);
        matchedSecond.GameId.Should().Be(thirdTicket.GameId);
        var waitingFirst = await GetAsync<MatchTicketView>(
            first,
            "/api/matchmaking/tickets/current");
        waitingFirst.TicketId.Should().Be(firstTicket.TicketId);
        waitingFirst.Status.Should().Be(MatchTicketStatus.Searching);

        await lockTransaction.CommitAsync();
    }

    [Fact]
    public async Task Final_ready_racing_with_ticket_creation_leaves_one_active_path()
    {
        using var creator = await CreatePlayerAsync();
        using var participant = await CreatePlayerAsync();
        var room = await PostAsync<RoomView>(
            creator,
            "/api/rooms",
            new CreateRoomRequest(GameState.CurrentRuleVersion, null));
        await PostAsync<RoomView>(participant, $"/api/rooms/{room.Code}/join", body: null);
        await PostAsync<RoomView>(
            creator,
            $"/api/rooms/{room.Code}/ready",
            new SetReadyRequest(true));
        const string requestId = "room-ready-race";

        var participantSession = await PostAsync<GuestSessionView>(participant, "/api/sessions/guest", body: null);
        await using var lockConnection = new NpgsqlConnection(database.ConnectionString);
        await lockConnection.OpenAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand(
            "SELECT 1 FROM guest_sessions WHERE id = @player_id FOR UPDATE",
            lockConnection,
            lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("player_id", participantSession.PlayerId);
            (await lockCommand.ExecuteScalarAsync()).Should().Be(1);
        }

        var finalReadyTask = PostResponseAsync(
            participant,
            $"/api/rooms/{room.Code}/ready",
            new SetReadyRequest(true));
        var ticketCreationTask = PostResponseAsync(
            participant,
            "/api/matchmaking/tickets",
            Ticket(requestId));

        await using var monitorConnection = new NpgsqlConnection(database.ConnectionString);
        await monitorConnection.OpenAsync();
        var blockedRequestCount = 0;
        await using (var blockingCommand = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM pg_stat_activity
            WHERE datname = current_database()
              AND pid <> pg_backend_pid()
              AND cardinality(pg_blocking_pids(pid)) > 0
            """,
            monitorConnection))
        {
            for (var attempt = 0; attempt < 200 && blockedRequestCount < 2; attempt++)
            {
                blockedRequestCount = Convert.ToInt32(await blockingCommand.ExecuteScalarAsync());
                if (blockedRequestCount < 2)
                {
                    await Task.Delay(25);
                }
            }
        }

        await lockTransaction.CommitAsync();
        var competing = await Task.WhenAll(finalReadyTask, ticketCreationTask);
        using var finalReady = competing[0];
        using var ticketCreation = competing[1];

        blockedRequestCount.Should().BeGreaterThanOrEqualTo(
            2,
            "both requests must reach their conflicting write before the coordinating locks are released");
        competing.Should().ContainSingle(response => response.StatusCode == HttpStatusCode.OK);
        competing.Should().OnlyContain(
            response => response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.Conflict);

        if (finalReady.StatusCode == HttpStatusCode.OK)
        {
            (await ReadAsync<RoomView>(finalReady)).Status.Should().Be(GameStatus.Playing);
            (await ReadAsync<ErrorResponse>(ticketCreation)).Code.Should().Be("ACTIVE_GAME_EXISTS");
        }
        else
        {
            (await ReadAsync<ErrorResponse>(finalReady)).Code.Should().Be("ACTIVE_TICKET_EXISTS");
            (await ReadAsync<MatchTicketView>(ticketCreation)).Status.Should()
                .BeOneOf(MatchTicketStatus.Searching, MatchTicketStatus.Matched);
        }

        await using var stateCommand = new NpgsqlCommand(
            """
            SELECT
                EXISTS (
                    SELECT 1
                    FROM rooms
                    WHERE code = @code
                      AND status = 'Playing'),
                EXISTS (
                    SELECT 1
                    FROM matchmaking_tickets AS ticket
                    INNER JOIN room_players AS player
                        ON player.player_id = ticket.player_id
                    INNER JOIN rooms AS ticket_room
                        ON ticket_room.id = player.room_id
                    WHERE ticket_room.code = @code
                      AND player.seat = 1
                      AND ticket.client_request_id = @request_id
                      AND ticket.status IN ('Searching', 'Matched'))
            """,
            lockConnection);
        stateCommand.Parameters.AddWithValue("code", room.Code);
        stateCommand.Parameters.AddWithValue("request_id", requestId);
        await using var stateReader = await stateCommand.ExecuteReaderAsync();
        (await stateReader.ReadAsync()).Should().BeTrue();
        var roomIsPlaying = stateReader.GetBoolean(0);
        var ticketIsActive = stateReader.GetBoolean(1);
        (roomIsPlaying && ticketIsActive).Should().BeFalse(
            "the room game and a new active ticket must be mutually exclusive");
        (roomIsPlaying || ticketIsActive).Should().BeTrue(
            "one of the two serialized operations must win");
    }

    [Fact]
    public async Task Matchmaking_is_idempotent_uses_fixed_quick_time_and_pairs_fifo_at_low_population()
    {
        using var first = await CreatePlayerAsync();
        using var second = await CreatePlayerAsync();
        using var third = await CreatePlayerAsync();
        using var fourth = await CreatePlayerAsync();

        var firstTicket = await PostAsync<MatchTicketView>(first, "/api/matchmaking/tickets", Ticket("first"));
        var repeated = await PostAsync<MatchTicketView>(first, "/api/matchmaking/tickets", Ticket("first"));
        repeated.TicketId.Should().Be(firstTicket.TicketId);

        var secondTicket = await PostAsync<MatchTicketView>(second, "/api/matchmaking/tickets", Ticket("second"));
        secondTicket.Status.Should().Be(MatchTicketStatus.Matched);
        secondTicket.TimeControl.Should().Be("600+5");
        secondTicket.MoveTimeLimitSeconds.Should().Be(90);
        var firstCurrent = await GetAsync<MatchTicketView>(first, "/api/matchmaking/tickets/current");
        firstCurrent.Status.Should().Be(MatchTicketStatus.Matched);
        firstCurrent.GameId.Should().Be(secondTicket.GameId);

        var thirdTicket = await PostAsync<MatchTicketView>(third, "/api/matchmaking/tickets", Ticket("third"));
        thirdTicket.Status.Should().Be(MatchTicketStatus.Searching);
        var fourthTicket = await PostAsync<MatchTicketView>(fourth, "/api/matchmaking/tickets", Ticket("fourth"));
        fourthTicket.Status.Should().Be(MatchTicketStatus.Matched);
        var thirdCurrent = await GetAsync<MatchTicketView>(third, "/api/matchmaking/tickets/current");
        thirdCurrent.GameId.Should().Be(fourthTicket.GameId);
        thirdCurrent.GameId.Should().NotBeNull();
        firstCurrent.GameId.Should().NotBeNull();
        thirdCurrent.GameId.Value.Should().NotBe(firstCurrent.GameId.Value);
    }

    [Fact]
    public async Task Matchmaking_prefers_the_closest_rating_before_waiting_order()
    {
        using var anchor = await CreatePlayerAsync();
        using var olderButFarther = await CreatePlayerAsync();
        using var close = await CreatePlayerAsync();
        using var exact = await CreatePlayerAsync();
        using var remaining = await CreatePlayerAsync();
        var anchorSession = await PostAsync<GuestSessionView>(anchor, "/api/sessions/guest", body: null);
        var fartherSession = await PostAsync<GuestSessionView>(olderButFarther, "/api/sessions/guest", body: null);
        var closeSession = await PostAsync<GuestSessionView>(close, "/api/sessions/guest", body: null);
        var exactSession = await PostAsync<GuestSessionView>(exact, "/api/sessions/guest", body: null);
        var remainingSession = await PostAsync<GuestSessionView>(remaining, "/api/sessions/guest", body: null);
        var now = DateTimeOffset.UtcNow;
        var tickets = new[]
        {
            (Guid.NewGuid(), anchorSession.PlayerId, 1500, 0),
            (Guid.NewGuid(), fartherSession.PlayerId, 1800, 1),
            (Guid.NewGuid(), closeSession.PlayerId, 1510, 2),
            (Guid.NewGuid(), exactSession.PlayerId, 1500, 3),
            (Guid.NewGuid(), remainingSession.PlayerId, 1490, 4)
        };

        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            foreach (var ticket in tickets)
            {
                await using var command = new NpgsqlCommand(
                    """
                    INSERT INTO matchmaking_tickets
                        (id, player_id, rule_version, time_control, move_time_limit_milliseconds,
                         rating_snapshot, status, created_at, last_heartbeat_at, expires_at,
                         client_request_id, concurrency_stamp)
                    VALUES
                        (@id, @player_id, @rule_version, '600+5', 90000, @rating, 'Searching',
                         @created_at, @now, @expires_at, @request_id, 0)
                    """,
                    connection);
                command.Parameters.AddWithValue("id", ticket.Item1);
                command.Parameters.AddWithValue("player_id", ticket.PlayerId);
                command.Parameters.AddWithValue("rule_version", GameState.CurrentRuleVersion);
                command.Parameters.AddWithValue("rating", ticket.Item3);
                command.Parameters.AddWithValue("created_at", now.AddSeconds(ticket.Item4));
                command.Parameters.AddWithValue("now", now);
                command.Parameters.AddWithValue("expires_at", now.AddMinutes(5));
                command.Parameters.AddWithValue("request_id", $"closest-{ticket.Item4}");
                (await command.ExecuteNonQueryAsync()).Should().Be(1);
            }
        }

        var coordinator = _factory.Services.GetRequiredService<MatchmakingCoordinator>();
        await coordinator.TryMatchAsync(CancellationToken.None);

        await using var resultConnection = new NpgsqlConnection(database.ConnectionString);
        await resultConnection.OpenAsync();
        await using var resultCommand = new NpgsqlCommand(
            """
            SELECT red_player_id, black_player_id
            FROM games
            WHERE red_player_id = @anchor OR black_player_id = @anchor
            """,
            resultConnection);
        resultCommand.Parameters.AddWithValue("anchor", anchorSession.PlayerId);
        await using var reader = await resultCommand.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        new[] { reader.GetGuid(0), reader.GetGuid(1) }.Should().BeEquivalentTo(
            new[] { anchorSession.PlayerId, exactSession.PlayerId });
        (await reader.ReadAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Guest_session_reports_the_active_game_until_it_finishes()
    {
        using var first = await CreatePlayerAsync();
        using var second = await CreatePlayerAsync();
        await PostAsync<MatchTicketView>(
            first,
            "/api/matchmaking/tickets",
            Ticket("active-session-first"));
        var matched = await PostAsync<MatchTicketView>(
            second,
            "/api/matchmaking/tickets",
            Ticket("active-session-second"));
        var gameId = matched.GameId!.Value;

        var activeSession = await PostAsync<GuestSessionView>(
            first,
            "/api/sessions/guest",
            body: null);
        activeSession.ActiveGameId.Should().Be(gameId);

        await PostAsync<ApiGameView>(first, $"/api/games/{gameId:D}/resign", body: null);
        var finishedSession = await PostAsync<GuestSessionView>(
            first,
            "/api/sessions/guest",
            body: null);
        finishedSession.ActiveGameId.Should().BeNull();
    }

    [Fact]
    public async Task Rated_quick_match_snapshots_ratings_and_settles_once()
    {
        using var first = await CreatePlayerAsync();
        using var second = await CreatePlayerAsync();
        var firstTicket = await PostAsync<MatchTicketView>(
            first,
            "/api/matchmaking/tickets",
            Ticket("rated-first"));
        var secondTicket = await PostAsync<MatchTicketView>(
            second,
            "/api/matchmaking/tickets",
            Ticket("rated-second"));
        var gameId = secondTicket.GameId!.Value;

        var finished = await PostAsync<ApiGameView>(first, $"/api/games/{gameId:D}/resign", body: null);
        var repeated = await PostAsync<ApiGameView>(first, $"/api/games/{gameId:D}/resign", body: null);
        var opponent = await GetAsync<ApiGameView>(second, $"/api/games/{gameId:D}");

        finished.TimeControl.Should().Be("600+5");
        finished.MoveTimeLimitSeconds.Should().Be(90);
        repeated.Version.Should().Be(finished.Version);
        repeated.Result.Should().Be(finished.Result);

        var firstSession = await PostAsync<GuestSessionView>(first, "/api/sessions/guest", body: null);
        var secondSession = await PostAsync<GuestSessionView>(second, "/api/sessions/guest", body: null);
        var history = await GetAsync<HistoricalGamesPageView>(first, "/api/games/history?limit=10");
        JsonSerializer.Serialize(finished, JsonOptions).Should().NotContain("rating");
        JsonSerializer.Serialize(opponent, JsonOptions).Should().NotContain("rating");
        JsonSerializer.Serialize(firstSession, JsonOptions).Should().NotContain("rating");
        JsonSerializer.Serialize(secondSession, JsonOptions).Should().NotContain("rating");
        JsonSerializer.Serialize(history, JsonOptions).Should().NotContain("rating");

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                (SELECT rating_snapshot FROM matchmaking_tickets WHERE id = @first_ticket),
                (SELECT rating_snapshot FROM matchmaking_tickets WHERE id = @second_ticket),
                (SELECT rating FROM player_ratings WHERE player_id = @first_player AND rule_version = @rule_version AND time_control = '600+5'),
                (SELECT rating FROM player_ratings WHERE player_id = @second_player AND rule_version = @rule_version AND time_control = '600+5'),
                (SELECT count(*) FROM rating_settlements WHERE game_id = @game_id)
            """,
            connection);
        command.Parameters.AddWithValue("first_ticket", firstTicket.TicketId);
        command.Parameters.AddWithValue("second_ticket", secondTicket.TicketId);
        command.Parameters.AddWithValue("game_id", gameId);
        command.Parameters.AddWithValue("first_player", firstSession.PlayerId);
        command.Parameters.AddWithValue("second_player", secondSession.PlayerId);
        command.Parameters.AddWithValue("rule_version", GameState.CurrentRuleVersion);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt32(0).Should().Be(1500);
        reader.GetInt32(1).Should().Be(1500);
        reader.GetInt32(2).Should().Be(1480);
        reader.GetInt32(3).Should().Be(1520);
        reader.GetInt64(4).Should().Be(1);
    }

    [Fact]
    public async Task Parallel_ticket_creation_claims_each_ticket_once()
    {
        using var first = await CreatePlayerAsync();
        using var second = await CreatePlayerAsync();

        var responses = await Task.WhenAll(
            PostAsync<MatchTicketView>(first, "/api/matchmaking/tickets", Ticket("parallel-a")),
            PostAsync<MatchTicketView>(second, "/api/matchmaking/tickets", Ticket("parallel-b")));

        var recovered = await Task.WhenAll(
            GetAsync<MatchTicketView>(first, "/api/matchmaking/tickets/current"),
            GetAsync<MatchTicketView>(second, "/api/matchmaking/tickets/current"));
        recovered.Should().OnlyContain(value => value.Status == MatchTicketStatus.Matched);
        recovered.Select(value => value.GameId).Distinct().Should().ContainSingle();
        responses.Select(value => value.TicketId).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task Ticket_heartbeat_cancellation_and_expiration_are_persisted()
    {
        using var activePlayer = await CreatePlayerAsync();
        var ticket = await PostAsync<MatchTicketView>(
            activePlayer,
            "/api/matchmaking/tickets",
            Ticket("lifecycle"));
        var heartbeat = await PostAsync<MatchTicketView>(
            activePlayer,
            $"/api/matchmaking/tickets/{ticket.TicketId:D}/heartbeat",
            body: null);
        heartbeat.ExpiresAt.Should().BeAfter(ticket.ExpiresAt);
        var cancelled = await DeleteAsync<MatchTicketView>(
            activePlayer,
            $"/api/matchmaking/tickets/{ticket.TicketId:D}");
        cancelled.Status.Should().Be(MatchTicketStatus.Cancelled);

        using var expiringPlayer = await CreatePlayerAsync();
        var expiring = await PostAsync<MatchTicketView>(
            expiringPlayer,
            "/api/matchmaking/tickets",
            Ticket("expiring"));
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                """
                UPDATE matchmaking_tickets
                SET created_at = now() - interval '3 minutes',
                    last_heartbeat_at = now() - interval '2 minutes',
                    expires_at = now() - interval '30 seconds'
                WHERE id = @id
                """,
                connection);
            command.Parameters.AddWithValue("id", expiring.TicketId);
            await command.ExecuteNonQueryAsync();
        }

        var expired = await PostAsync<MatchTicketView>(
            expiringPlayer,
            $"/api/matchmaking/tickets/{expiring.TicketId:D}/heartbeat",
            body: null);
        expired.Status.Should().Be(MatchTicketStatus.Expired);
    }

    [Fact]
    public async Task History_is_private_paginated_and_contains_switchable_safe_views()
    {
        using var first = await CreatePlayerAsync();
        using var second = await CreatePlayerAsync();
        using var outsider = await CreatePlayerAsync();
        var firstGame = await CreateFinishedRoomGameAsync(first, second, makeMove: true);
        var secondGame = await CreateFinishedRoomGameAsync(first, second, makeMove: false);

        using var firstPageResponse = await first.GetAsync("/api/games/history?limit=1");
        firstPageResponse.EnsureSuccessStatusCode();
        firstPageResponse.Headers.CacheControl!.Private.Should().BeTrue();
        firstPageResponse.Headers.CacheControl.NoStore.Should().BeTrue();
        var firstPage = await ReadAsync<HistoricalGamesPageView>(firstPageResponse);
        firstPage.Games.Should().ContainSingle();
        firstPage.NextCursor.Should().NotBeNullOrWhiteSpace();
        var secondPage = await GetAsync<HistoricalGamesPageView>(
            first,
            $"/api/games/history?limit=1&cursor={Uri.EscapeDataString(firstPage.NextCursor!)}");
        firstPage.Games.Concat(secondPage.Games).Select(game => game.GameId)
            .Should().BeEquivalentTo(new[] { firstGame.GameId, secondGame.GameId });
        secondPage.NextCursor.Should().BeNull();

        var losses = await GetAsync<HistoricalGamesPageView>(
            first,
            "/api/games/history?limit=20&result=loss&timeControl=untimed");
        losses.Games.Should().HaveCount(2);
        losses.Games.Should().OnlyContain(game =>
            (game.CurrentPlayerSide == Side.Red ? game.Red.Outcome : game.Black.Outcome) == HistoricalOutcome.Loss);
        (await GetAsync<HistoricalGamesPageView>(outsider, "/api/games/history"))
            .Games.Should().BeEmpty();

        using var firstReplayResponse = await first.GetAsync($"/api/games/{firstGame.GameId:D}/replay");
        firstReplayResponse.EnsureSuccessStatusCode();
        firstReplayResponse.Headers.CacheControl!.Private.Should().BeTrue();
        firstReplayResponse.Headers.CacheControl.NoCache.Should().BeTrue();
        firstReplayResponse.Headers.Vary.Should().Contain("Cookie");
        var etag = firstReplayResponse.Headers.ETag!.Tag;
        var firstReplayJson = await firstReplayResponse.Content.ReadAsStringAsync();
        var firstReplay = JsonSerializer.Deserialize<HistoricalReplayView>(
            firstReplayJson,
            JsonOptions) ?? throw new InvalidOperationException("The replay response was empty.");
        firstReplayJson.Should().NotContain("clientMoveId");
        firstReplayJson.Should().NotContain("guestSessionId");
        firstReplayJson.Should().NotContain("ownerPlayerId");
        firstReplayJson.Should().NotContain("tokenHash");
        firstReplayJson.Should().NotContain("roomCode");
        using var compressedRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/games/{firstGame.GameId:D}/replay");
        compressedRequest.Headers.AcceptEncoding.ParseAdd("gzip");
        using var compressedResponse = await first.SendAsync(compressedRequest);
        compressedResponse.EnsureSuccessStatusCode();
        compressedResponse.Content.Headers.ContentEncoding.Should().Contain("gzip");
        var compressedBytes = await compressedResponse.Content.ReadAsByteArrayAsync();
        compressedBytes.Length.Should().BeLessThan(Encoding.UTF8.GetByteCount(firstReplayJson));
        var secondReplay = await GetAsync<HistoricalReplayView>(
            second,
            $"/api/games/{firstGame.GameId:D}/replay");
        firstReplay.CurrentPlayerSide.Should().NotBe(secondReplay.CurrentPlayerSide);
        firstReplay.Frames.Should().HaveCount(3);
        var moveFrame = firstReplay.Frames[1];
        moveFrame.Views.Omniscient.VisibleSquares.Should().HaveCount(90);
        moveFrame.Views.Omniscient.Move.Should().NotBeNull();
        var moverView = firstGame.MovedSide == Side.Red ? moveFrame.Views.Red : moveFrame.Views.Black;
        var opponentView = firstGame.MovedSide == Side.Red ? moveFrame.Views.Black : moveFrame.Views.Red;
        moverView.Move.Should().NotBeNull();
        opponentView.Move.Should().BeNull();
        moveFrame.Views.Red.Pieces.Count(piece => piece.Side == Side.Red).Should().Be(16);
        moveFrame.Views.Black.Pieces.Count(piece => piece.Side == Side.Black).Should().Be(16);
        moveFrame.Views.Red.VisibleSquares.Should().HaveCountLessThan(90);
        moveFrame.Views.Black.VisibleSquares.Should().HaveCountLessThan(90);

        using var conditionalRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/games/{firstGame.GameId:D}/replay");
        conditionalRequest.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var unchanged = await first.SendAsync(conditionalRequest);
        unchanged.StatusCode.Should().Be(HttpStatusCode.NotModified);

        using var anonymous = _factory.CreateHttpsClient();
        using var unauthorizedRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/games/{firstGame.GameId:D}/replay");
        unauthorizedRequest.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var unauthorized = await anonymous.SendAsync(unauthorizedRequest);
        unauthorized.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await outsider.GetAsync($"/api/games/{firstGame.GameId:D}/replay"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Replay_shares_rotate_revoke_independently_and_need_no_session()
    {
        using var first = await CreatePlayerAsync();
        using var second = await CreatePlayerAsync();
        using var outsider = await CreatePlayerAsync();
        var game = await CreateFinishedRoomGameAsync(first, second, makeMove: false);
        var firstShare = await PostAsync<ReplayShareCreatedView>(
            first,
            $"/api/games/{game.GameId:D}/replay-share",
            body: null);
        var firstToken = firstShare.SharePath[(firstShare.SharePath.LastIndexOf('/') + 1)..];
        firstToken.Should().HaveLength(43);

        using var anonymous = _factory.CreateHttpsClient();
        using var sharedResponse = await anonymous.GetAsync($"/api/replay-shares/{firstToken}");
        sharedResponse.EnsureSuccessStatusCode();
        sharedResponse.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await ReadAsync<HistoricalReplayView>(sharedResponse)).CurrentPlayerSide.Should().BeNull();

        var rotatedShare = await PostAsync<ReplayShareCreatedView>(
            first,
            $"/api/games/{game.GameId:D}/replay-share",
            body: null);
        var rotatedToken = rotatedShare.SharePath[(rotatedShare.SharePath.LastIndexOf('/') + 1)..];
        rotatedToken.Should().NotBe(firstToken);
        (await anonymous.GetAsync($"/api/replay-shares/{firstToken}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await anonymous.GetAsync($"/api/replay-shares/{rotatedToken}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var secondShare = await PostAsync<ReplayShareCreatedView>(
            second,
            $"/api/games/{game.GameId:D}/replay-share",
            body: null);
        var secondToken = secondShare.SharePath[(secondShare.SharePath.LastIndexOf('/') + 1)..];
        using var revoked = await first.DeleteAsync($"/api/games/{game.GameId:D}/replay-share");
        revoked.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await anonymous.GetAsync($"/api/replay-shares/{rotatedToken}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await anonymous.GetAsync($"/api/replay-shares/{secondToken}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await first.DeleteAsync($"/api/games/{game.GameId:D}/replay-share"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await PostResponseAsync(
            outsider,
            $"/api/games/{game.GameId:D}/replay-share",
            body: null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Shared_replay_rate_limit_is_partitioned_by_token()
    {
        using var first = await CreatePlayerAsync();
        using var second = await CreatePlayerAsync();
        var game = await CreateFinishedRoomGameAsync(first, second, makeMove: false);
        var share = await PostAsync<ReplayShareCreatedView>(
            first,
            $"/api/games/{game.GameId:D}/replay-share",
            body: null);
        var token = share.SharePath[(share.SharePath.LastIndexOf('/') + 1)..];
        using var anonymous = _factory.CreateHttpsClient();

        for (var requestNumber = 0; requestNumber < 60; requestNumber++)
        {
            using var allowed = await anonymous.GetAsync($"/api/replay-shares/{token}");
            allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var rejected = await anonymous.GetAsync($"/api/replay-shares/{token}");
        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await ReadAsync<ErrorResponse>(rejected)).Code.Should().Be("RATE_LIMITED");

        var rotated = await PostAsync<ReplayShareCreatedView>(
            first,
            $"/api/games/{game.GameId:D}/replay-share",
            body: null);
        var rotatedToken = rotated.SharePath[(rotated.SharePath.LastIndexOf('/') + 1)..];
        using var independent = await anonymous.GetAsync($"/api/replay-shares/{rotatedToken}");
        independent.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task History_query_uses_partial_player_indexes_with_realistic_volume()
    {
        using var targetRed = await CreatePlayerAsync();
        using var targetBlack = await CreatePlayerAsync();
        using var noiseRed = await CreatePlayerAsync();
        using var noiseBlack = await CreatePlayerAsync();
        var targetGame = await CreateFinishedRoomGameAsync(targetRed, targetBlack, makeMove: false);
        var noiseGame = await CreateFinishedRoomGameAsync(noiseRed, noiseBlack, makeMove: false);

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        var targetPlayerId = Guid.Empty;
        await using (var playerCommand = new NpgsqlCommand(
            "SELECT red_player_id FROM games WHERE id = @id",
            connection))
        {
            playerCommand.Parameters.AddWithValue("id", targetGame.GameId);
            targetPlayerId = (Guid)(await playerCommand.ExecuteScalarAsync()
                ?? throw new InvalidOperationException("The target game was not persisted."));
        }

        const string insertSql =
            """
            INSERT INTO games
                (id, red_player_id, black_player_id, initial_state, state, side_to_move,
                 status, winner, result_reason, rule_version, time_control, is_rated,
                 red_milliseconds, black_milliseconds, turn_started_at, clock_expires_at,
                 version, created_at, updated_at, finished_at)
            SELECT
                gen_random_uuid(), source.red_player_id, source.black_player_id,
                '{}'::jsonb, '{}'::jsonb, 'Red', 'Finished', 'Red', 'Resignation',
                source.rule_version, NULL, FALSE, NULL, NULL, NULL, NULL, 0,
                now() - (series.value * interval '1 second'),
                now() - (series.value * interval '1 second'),
                now() - (series.value * interval '1 second')
            FROM games AS source
            CROSS JOIN generate_series(1, @row_count) AS series(value)
            WHERE source.id = @source_id
            """;
        await using (var targetInsert = new NpgsqlCommand(insertSql, connection))
        {
            targetInsert.Parameters.AddWithValue("row_count", 400);
            targetInsert.Parameters.AddWithValue("source_id", targetGame.GameId);
            (await targetInsert.ExecuteNonQueryAsync()).Should().Be(400);
        }

        await using (var noiseInsert = new NpgsqlCommand(insertSql, connection))
        {
            noiseInsert.Parameters.AddWithValue("row_count", 20_000);
            noiseInsert.Parameters.AddWithValue("source_id", noiseGame.GameId);
            (await noiseInsert.ExecuteNonQueryAsync()).Should().Be(20_000);
        }

        await using (var analyze = new NpgsqlCommand("ANALYZE games", connection))
        {
            await analyze.ExecuteNonQueryAsync();
        }

        await using var explain = new NpgsqlCommand(
            """
            EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)
            SELECT id, finished_at
            FROM games
            WHERE status = 'Finished'
              AND (red_player_id = @player_id OR black_player_id = @player_id)
            ORDER BY finished_at DESC, id DESC
            LIMIT 21
            """,
            connection);
        explain.Parameters.AddWithValue("player_id", targetPlayerId);
        var plan = (string)(await explain.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("PostgreSQL did not return an execution plan."));

        (plan.Contains("ix_games_history_red", StringComparison.Ordinal) ||
         plan.Contains("ix_games_history_black", StringComparison.Ordinal))
            .Should().BeTrue(plan);
        plan.Should().NotContain("\"Node Type\": \"Seq Scan\"");
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static CreateMatchTicketRequest Ticket(string requestId) =>
        new(GameState.CurrentRuleVersion, requestId);

    private async Task<Guid> CreateStartedRoomGameAsync(
        HttpClient first,
        HttpClient second,
        string? timeControl = null,
        int? moveTimeLimitSeconds = null)
    {
        var room = await PostAsync<RoomView>(
            first,
            "/api/rooms",
            new CreateRoomRequest(GameState.CurrentRuleVersion, timeControl, moveTimeLimitSeconds));
        await PostAsync<RoomView>(second, $"/api/rooms/{room.Code}/join", body: null);
        await PostAsync<RoomView>(
            first,
            $"/api/rooms/{room.Code}/ready",
            new SetReadyRequest(true));
        var started = await PostAsync<RoomView>(
            second,
            $"/api/rooms/{room.Code}/ready",
            new SetReadyRequest(true));
        started.Status.Should().Be(GameStatus.Playing);
        started.GameId.Should().NotBeNull();
        return started.GameId!.Value;
    }

    private static async Task<TurnContext> GetCurrentTurnAsync(
        HttpClient first,
        HttpClient second,
        Guid gameId)
    {
        var firstView = await GetAsync<ApiGameView>(first, $"/api/games/{gameId:D}");
        var secondView = await GetAsync<ApiGameView>(second, $"/api/games/{gameId:D}");
        return firstView.Perspective == firstView.SideToMove
            ? new TurnContext(first, second, firstView, secondView)
            : new TurnContext(second, first, secondView, firstView);
    }

    private static Task<ApiGameView> PlayCandidateMoveAsync(
        Guid gameId,
        TurnContext turn,
        string clientMoveId)
    {
        var candidate = turn.MoverView.CandidateMoves.First(move => move.Destinations.Count > 0);
        return PostAsync<ApiGameView>(
            turn.Mover,
            $"/api/games/{gameId:D}/moves",
            new MoveRequest(
                candidate.From,
                candidate.Destinations[0],
                turn.MoverView.Version,
                clientMoveId));
    }

    private sealed record TurnContext(
        HttpClient Mover,
        HttpClient Opponent,
        ApiGameView MoverView,
        ApiGameView OpponentView);

    private async Task ExpireCurrentTurnAsync(Guid gameId)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE games
            SET turn_started_at = now() - interval '5 seconds',
                clock_expires_at = now() - interval '1 millisecond',
                red_milliseconds = CASE WHEN side_to_move = 'Red' THEN 1 ELSE red_milliseconds END,
                black_milliseconds = CASE WHEN side_to_move = 'Black' THEN 1 ELSE black_milliseconds END
            WHERE id = @id
            """,
            connection);
        command.Parameters.AddWithValue("id", gameId);
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    private async Task AssertTimeoutPersistedAsync(Guid gameId, Side expectedWinner, Side timedOutSide)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT g.status,
                   g.winner,
                   g.result_reason,
                   g.red_milliseconds,
                   g.black_milliseconds,
                   r.status,
                   (SELECT count(*)
                    FROM game_players AS gp
                    WHERE gp.game_id = g.id AND gp.is_active)
            FROM games AS g
            INNER JOIN rooms AS r ON r.game_id = g.id
            WHERE g.id = @id
            """,
            connection);
        command.Parameters.AddWithValue("id", gameId);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be(GameStatus.Finished.ToString());
        reader.GetString(1).Should().Be(expectedWinner.ToString());
        reader.GetString(2).Should().Be(GameResultReason.Timeout.ToString());
        reader.GetInt64(timedOutSide == Side.Red ? 3 : 4).Should().Be(0);
        reader.GetString(5).Should().Be(GameStatus.Finished.ToString());
        reader.GetInt64(6).Should().Be(0);
        (await reader.ReadAsync()).Should().BeFalse();
    }

    private async Task<(Guid GameId, Side? MovedSide)> CreateFinishedRoomGameAsync(
        HttpClient first,
        HttpClient second,
        bool makeMove)
    {
        var gameId = await CreateStartedRoomGameAsync(first, second);
        Side? movedSide = null;
        if (makeMove)
        {
            var firstView = await GetAsync<ApiGameView>(first, $"/api/games/{gameId:D}");
            var secondView = await GetAsync<ApiGameView>(second, $"/api/games/{gameId:D}");
            var movingClient = firstView.Perspective == firstView.SideToMove ? first : second;
            var movingView = firstView.Perspective == firstView.SideToMove ? firstView : secondView;
            var candidate = movingView.CandidateMoves.First(move => move.Destinations.Count > 0);
            await PostAsync<ApiGameView>(
                movingClient,
                $"/api/games/{gameId:D}/moves",
                new MoveRequest(
                    candidate.From,
                    candidate.Destinations[0],
                    movingView.Version,
                    $"history-{gameId:N}"));
            movedSide = movingView.SideToMove;
        }

        await PostAsync<ApiGameView>(first, $"/api/games/{gameId:D}/resign", body: null);
        return (gameId, movedSide);
    }

    private async Task<HttpClient> CreatePlayerAsync()
    {
        var client = _factory.CreateHttpsClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "MistChess");
        var session = await client.PostAsync("/api/sessions/guest", null);
        session.EnsureSuccessStatusCode();
        var tokenResponse = await client.GetAsync("/api/antiforgery/token");
        tokenResponse.EnsureSuccessStatusCode();
        var token = await ReadAsync<AntiforgeryTokenView>(tokenResponse);
        client.DefaultRequestHeaders.Add(token.HeaderName, token.Token);
        return client;
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await ReadAsync<T>(response);
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string path, object? body)
    {
        var response = await PostResponseAsync(client, path, body);
        response.EnsureSuccessStatusCode();
        return await ReadAsync<T>(response);
    }

    private static Task<HttpResponseMessage> PostResponseAsync(HttpClient client, string path, object? body) =>
        client.PostAsync(path, body is null ? null : JsonContent.Create(body, options: JsonOptions));

    private static async Task<T> DeleteAsync<T>(HttpClient client, string path)
    {
        var response = await client.DeleteAsync(path);
        response.EnsureSuccessStatusCode();
        return await ReadAsync<T>(response);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException("The API response body was empty.");
}
