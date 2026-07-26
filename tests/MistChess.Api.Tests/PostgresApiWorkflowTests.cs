using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
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

        var firstReplay = await GetAsync<ReplayView>(first, $"/api/games/{gameId:D}/replay");
        var secondReplay = await GetAsync<ReplayView>(second, $"/api/games/{gameId:D}/replay");
        firstReplay.Result.Should().BeEquivalentTo(secondReplay.Result);
        firstReplay.Frames.Should().HaveCount(2);
        firstReplay.Frames[0].Pieces.Should().HaveCount(32);
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
    public async Task Accepting_draw_after_current_clock_expires_finishes_by_timeout()
    {
        using var first = await CreatePlayerAsync();
        using var second = await CreatePlayerAsync();
        var room = await PostAsync<RoomView>(
            first,
            "/api/rooms",
            new CreateRoomRequest(GameState.CurrentRuleVersion, "60+0"));
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
            new CreateRoomRequest(GameState.CurrentRuleVersion, "60+0"));
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
            new CreateRoomRequest(GameState.CurrentRuleVersion, "60+0"));
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
        var replay = await GetAsync<ReplayView>(movingClient, $"/api/games/{gameId:D}/replay");
        replay.Frames.Should().ContainSingle();
        replay.Frames[0].Move.Should().BeNull();

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
        const string timeControl = "75+0";
        var firstTicket = await PostAsync<MatchTicketView>(
            first,
            "/api/matchmaking/tickets",
            Ticket("cancel-race-a", timeControl));

        var competing = await Task.WhenAll(
            first.DeleteAsync($"/api/matchmaking/tickets/{firstTicket.TicketId:D}"),
            PostResponseAsync(
                second,
                "/api/matchmaking/tickets",
                Ticket("cancel-race-b", timeControl)));
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
            Ticket(requestId, null));

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
    public async Task Matchmaking_is_idempotent_pool_exact_fifo_and_atomic()
    {
        using var first = await CreatePlayerAsync();
        using var second = await CreatePlayerAsync();
        using var third = await CreatePlayerAsync();
        using var fourth = await CreatePlayerAsync();

        var firstTicket = await PostAsync<MatchTicketView>(first, "/api/matchmaking/tickets", Ticket("first", null));
        var repeated = await PostAsync<MatchTicketView>(first, "/api/matchmaking/tickets", Ticket("first", null));
        repeated.TicketId.Should().Be(firstTicket.TicketId);

        var secondTicket = await PostAsync<MatchTicketView>(second, "/api/matchmaking/tickets", Ticket("second", "60+0"));
        secondTicket.Status.Should().Be(MatchTicketStatus.Searching);
        var thirdTicket = await PostAsync<MatchTicketView>(third, "/api/matchmaking/tickets", Ticket("third", null));
        thirdTicket.Status.Should().Be(MatchTicketStatus.Matched);

        var firstCurrent = await GetAsync<MatchTicketView>(first, "/api/matchmaking/tickets/current");
        firstCurrent.Status.Should().Be(MatchTicketStatus.Matched);
        firstCurrent.GameId.Should().Be(thirdTicket.GameId);

        var fourthTicket = await PostAsync<MatchTicketView>(fourth, "/api/matchmaking/tickets", Ticket("fourth", "60+0"));
        fourthTicket.Status.Should().Be(MatchTicketStatus.Matched);
        var secondCurrent = await GetAsync<MatchTicketView>(second, "/api/matchmaking/tickets/current");
        secondCurrent.GameId.Should().Be(fourthTicket.GameId);
        secondCurrent.GameId.Should().NotBeNull();
        firstCurrent.GameId.Should().NotBeNull();
        secondCurrent.GameId.Value.Should().NotBe(firstCurrent.GameId.Value);
    }

    [Fact]
    public async Task Parallel_ticket_creation_claims_each_ticket_once()
    {
        using var first = await CreatePlayerAsync();
        using var second = await CreatePlayerAsync();

        var responses = await Task.WhenAll(
            PostAsync<MatchTicketView>(first, "/api/matchmaking/tickets", Ticket("parallel-a", null)),
            PostAsync<MatchTicketView>(second, "/api/matchmaking/tickets", Ticket("parallel-b", null)));

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
            Ticket("lifecycle", null));
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
            Ticket("expiring", null));
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

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static CreateMatchTicketRequest Ticket(string requestId, string? timeControl) =>
        new(GameState.CurrentRuleVersion, timeControl, requestId);

    private async Task ExpireCurrentTurnAsync(Guid gameId)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE games
            SET turn_started_at = now() - interval '5 seconds',
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
