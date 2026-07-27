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
    public async Task Draw_offer_can_be_rejected_then_accepted_and_ends_the_game()
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

        var offered = await PostAsync<DrawOfferView>(
            first,
            $"/api/games/{gameId:D}/draw-offers",
            body: null);
        offered.Status.Should().Be(DrawOfferStatus.Pending);
        var rejected = await PostAsync<DrawOfferView>(
            second,
            $"/api/games/{gameId:D}/draw-offers/reject",
            body: null);
        rejected.Status.Should().Be(DrawOfferStatus.Rejected);

        await PostAsync<DrawOfferView>(first, $"/api/games/{gameId:D}/draw-offers", body: null);
        var accepted = await PostAsync<ApiGameView>(
            second,
            $"/api/games/{gameId:D}/draw-offers/accept",
            body: null);
        accepted.Status.Should().Be(GameStatus.Finished);
        accepted.Result.Should().Be(new GameResultView(null, GameResultReason.AgreedDraw));
        accepted.VisibleSquares.Should().HaveCount(90);
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
        var room = await PostAsync<RoomView>(
            first,
            "/api/rooms",
            new CreateRoomRequest(GameState.CurrentRuleVersion, null));
        await PostAsync<RoomView>(second, $"/api/rooms/{room.Code}/join", body: null);
        await PostAsync<RoomView>(
            first,
            $"/api/rooms/{room.Code}/ready",
            new SetReadyRequest(true));
        var started = await PostAsync<RoomView>(
            second,
            $"/api/rooms/{room.Code}/ready",
            new SetReadyRequest(true));
        var gameId = started.GameId!.Value;
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
