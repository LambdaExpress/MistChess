using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using MistChess.Api.Contracts;
using MistChess.Api.Security;
using MistChess.Domain;
using MistChess.Api.Tests.Infrastructure;
using Npgsql;

namespace MistChess.Api.Tests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class Phase3AdminTests(PostgresDatabaseFixture database) : IAsyncLifetime
{
    private const string AdminUsername = "phase3-admin";
    private const string AdminPassword = "correct horse battery staple";
    private static readonly DateTimeOffset InitialTime = new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private ManualTimeProvider _clock = null!;
    private MistChessWebApplicationFactory _factory = null!;

    public async Task InitializeAsync()
    {
        await database.ResetAsync();
        _clock = new ManualTimeProvider(InitialTime);
        _factory = new MistChessWebApplicationFactory(
            database.ConnectionString,
            settings: CreateAdminSettings(),
            timeProvider: _clock);
    }

    [Fact]
    public async Task Configured_admin_can_log_in_and_read_the_session()
    {
        using var admin = _factory.CreateHttpsClient();
        await AddAdminAntiforgeryTokenAsync(admin);

        var response = await admin.PostAsJsonAsync(
            "/api/admin/session",
            new AdminLoginRequest(AdminUsername, AdminPassword),
            JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var loginBody = await response.Content.ReadAsStringAsync();
        var created = Deserialize<AdminSessionView>(loginBody);
        created.Username.Should().Be(AdminUsername);
        created.ExpiresAt.Should().Be(InitialTime.AddHours(8));
        response.Headers.CacheControl?.NoStore.Should().BeTrue();
        loginBody.Contains("passwordHash", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        loginBody.Contains("ticket", StringComparison.OrdinalIgnoreCase).Should().BeFalse();

        var currentResponse = await admin.GetAsync("/api/admin/session");
        currentResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync<AdminSessionView>(currentResponse)).Should().BeEquivalentTo(created);
    }

    [Fact]
    public async Task Invalid_configured_password_hash_returns_a_generic_disabled_error()
    {
        using var invalidFactory = new MistChessWebApplicationFactory(
            database.ConnectionString,
            settings: new Dictionary<string, string?>
            {
                ["Admin:Username"] = AdminUsername,
                ["Admin:PasswordHash"] = "not-a-password-hash"
            },
            timeProvider: _clock);
        using var admin = invalidFactory.CreateHttpsClient();
        await AddAdminAntiforgeryTokenAsync(admin);

        var response = await admin.PostAsJsonAsync(
            "/api/admin/session",
            new AdminLoginRequest(AdminUsername, AdminPassword),
            JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var responseBody = await response.Content.ReadAsStringAsync();
        Deserialize<ErrorResponse>(responseBody).Code.Should().Be("ADMIN_LOGIN_DISABLED");
        responseBody.Should().NotContain("not-a-password-hash");
    }

    [Theory]
    [InlineData("not-the-admin", AdminPassword)]
    [InlineData(AdminUsername, "wrong password")]
    public async Task Unknown_username_and_wrong_password_return_the_same_generic_error(
        string username,
        string password)
    {
        using var admin = _factory.CreateHttpsClient();
        await AddAdminAntiforgeryTokenAsync(admin);

        var response = await admin.PostAsJsonAsync(
            "/api/admin/session",
            new AdminLoginRequest(username, password),
            JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var responseBody = await response.Content.ReadAsStringAsync();
        var error = Deserialize<ErrorResponse>(responseBody);
        error.Code.Should().Be("INVALID_ADMIN_CREDENTIALS");
        error.Title.Should().Be("The administrator username or password is invalid.");
        responseBody.Should().NotContain(AdminUsername);
        response.Headers.Location.Should().BeNull();
    }

    [Fact]
    public async Task Five_failed_logins_are_returned_then_the_sixth_is_rate_limited()
    {
        using var admin = _factory.CreateHttpsClient();
        await AddAdminAntiforgeryTokenAsync(admin);

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var failed = await admin.PostAsJsonAsync(
                "/api/admin/session",
                new AdminLoginRequest(AdminUsername, $"wrong-{attempt}"),
                JsonOptions);
            failed.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"failure {attempt} is still observable as a credential failure");
            (await ReadAsync<ErrorResponse>(failed)).Code.Should().Be("INVALID_ADMIN_CREDENTIALS");
        }

        var blocked = await admin.PostAsJsonAsync(
            "/api/admin/session",
            new AdminLoginRequest(AdminUsername, AdminPassword),
            JsonOptions);
        blocked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await ReadAsync<ErrorResponse>(blocked)).Code.Should().Be("RATE_LIMITED");
    }

    [Fact]
    public async Task Admin_login_and_logout_require_antiforgery_and_logout_invalidates_the_cookie()
    {
        using var admin = _factory.CreateHttpsClient();

        var unprotectedLogin = await admin.PostAsJsonAsync(
            "/api/admin/session",
            new AdminLoginRequest(AdminUsername, AdminPassword),
            JsonOptions);
        unprotectedLogin.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var loginToken = await AddAdminAntiforgeryTokenAsync(admin);
        var login = await admin.PostAsJsonAsync(
            "/api/admin/session",
            new AdminLoginRequest(AdminUsername, AdminPassword),
            JsonOptions);
        login.StatusCode.Should().Be(HttpStatusCode.OK);

        admin.DefaultRequestHeaders.Remove(loginToken.HeaderName).Should().BeTrue();
        var unprotectedLogout = await admin.DeleteAsync("/api/admin/session");
        unprotectedLogout.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await AddAdminAntiforgeryTokenAsync(admin);
        var logout = await admin.DeleteAsync("/api/admin/session");
        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var afterLogout = await admin.GetAsync("/api/admin/session");
        afterLogout.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadAsync<ErrorResponse>(afterLogout)).Code.Should().Be("UNAUTHORIZED");
        afterLogout.Headers.Location.Should().BeNull();
    }

    [Fact]
    public async Task Guest_cookie_cannot_authorize_admin_session_or_user_data()
    {
        using var guest = await CreatePlayerAsync();

        var session = await guest.Client.GetAsync("/api/admin/session");
        var users = await guest.Client.GetAsync("/api/admin/users");
        var detail = await guest.Client.GetAsync($"/api/admin/users/{guest.Session.PlayerId:D}");
        var history = await guest.Client.GetAsync($"/api/admin/users/{guest.Session.PlayerId:D}/games");
        var replay = await guest.Client.GetAsync($"/api/admin/games/{Guid.NewGuid():D}/replay");

        foreach (var response in new[] { session, users, detail, history, replay })
        {
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await ReadAsync<ErrorResponse>(response)).Code.Should().Be("UNAUTHORIZED");
            response.Headers.Location.Should().BeNull();
        }
    }

    [Fact]
    public async Task Heartbeat_writes_only_after_the_thirty_second_throttle_window()
    {
        using var player = await CreatePlayerAsync();
        var priorSeenAt = InitialTime.AddSeconds(-29);
        await SetLastSeenAtAsync(player.Session.PlayerId, priorSeenAt);

        var throttled = await player.Client.PostAsync("/api/sessions/heartbeat", null);
        throttled.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await GetLastSeenAtAsync(player.Session.PlayerId)).Should().Be(priorSeenAt);

        _clock.Advance(TimeSpan.FromSeconds(2));
        var touched = await player.Client.PostAsync("/api/sessions/heartbeat", null);
        touched.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await GetLastSeenAtAsync(player.Session.PlayerId)).Should().Be(_clock.GetUtcNow());
    }

    [Fact]
    public async Task Banned_guest_bootstrap_and_heartbeat_are_forbidden_without_creating_a_new_identity()
    {
        using var player = await CreatePlayerAsync();
        await BanDirectlyAsync(player.Session.PlayerId, "phase three moderation");

        var bootstrap = await player.Client.PostAsync("/api/sessions/guest", null);
        bootstrap.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var bootstrapError = await ReadAsync<ErrorResponse>(bootstrap);
        bootstrapError.Code.Should().Be("PLAYER_BANNED");
        bootstrapError.Detail.Should().Be("phase three moderation");

        var heartbeat = await player.Client.PostAsync("/api/sessions/heartbeat", null);
        heartbeat.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var heartbeatError = await ReadAsync<ErrorResponse>(heartbeat);
        heartbeatError.Code.Should().Be("PLAYER_BANNED");
        heartbeatError.Detail.Should().Be("phase three moderation");

        (await CountGuestSessionsAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Admin_user_list_applies_search_status_online_cursor_and_primary_rating_rules()
    {
        using var alpha = await CreatePlayerAsync();
        using var beta = await CreatePlayerAsync();
        using var offline = await CreatePlayerAsync();
        using var banned = await CreatePlayerAsync();
        await SetProfileAsync(alpha.Session.PlayerId, "Alpha Sentinel", InitialTime.AddSeconds(-10));
        await SetProfileAsync(beta.Session.PlayerId, "Beta Scout", InitialTime.AddSeconds(-20));
        await SetProfileAsync(offline.Session.PlayerId, "Dormant Alpha", InitialTime.AddSeconds(-91));
        await SetProfileAsync(banned.Session.PlayerId, "Banned Alpha", InitialTime.AddSeconds(-5), banned: true);
        await SetPrimaryRatingAsync(alpha.Session.PlayerId, 1612, gamesPlayed: 5, wins: 3, draws: 1, losses: 1);
        using var admin = await CreateAdminAsync();

        var search = await GetAsync<AdminUsersPageView>(
            admin,
            "/api/admin/users?query=ALPHA&status=active&online=all&limit=20");
        search.Items.Select(item => item.PlayerId).Should().BeEquivalentTo(
            new[] { alpha.Session.PlayerId, offline.Session.PlayerId });
        search.Items.Should().NotContain(item => item.PlayerId == banned.Session.PlayerId);

        var online = await GetAsync<AdminUsersPageView>(
            admin,
            "/api/admin/users?status=all&online=online&limit=20");
        online.ObservedAt.Should().Be(InitialTime);
        online.Items.Select(item => item.PlayerId).Should().BeEquivalentTo(
            new[] { alpha.Session.PlayerId, beta.Session.PlayerId });

        var offlinePage = await GetAsync<AdminUsersPageView>(
            admin,
            "/api/admin/users?status=all&online=offline&limit=20");
        offlinePage.Items.Select(item => item.PlayerId).Should().BeEquivalentTo(
            new[] { offline.Session.PlayerId, banned.Session.PlayerId });

        var bannedPage = await GetAsync<AdminUsersPageView>(
            admin,
            "/api/admin/users?status=banned&online=all&limit=20");
        bannedPage.Items.Should().ContainSingle().Which.PlayerId.Should().Be(banned.Session.PlayerId);

        var byId = await GetAsync<AdminUsersPageView>(
            admin,
            $"/api/admin/users?query={alpha.Session.PlayerId:D}&status=all&online=all&limit=20");
        var rated = byId.Items.Should().ContainSingle().Which;
        rated.Rating.Should().Be(1612);
        rated.GamesPlayed.Should().Be(5);
        rated.Wins.Should().Be(3);
        rated.Draws.Should().Be(1);
        rated.Losses.Should().Be(1);
        rated.WinRate.Should().Be(60.0m);

        var unrated = (await GetAsync<AdminUsersPageView>(
            admin,
            $"/api/admin/users?query={beta.Session.PlayerId:D}&status=all&online=all&limit=20"))
            .Items.Should().ContainSingle().Which;
        unrated.Rating.Should().Be(1500);
        unrated.GamesPlayed.Should().Be(0);
        unrated.WinRate.Should().BeNull();

        var firstPage = await GetAsync<AdminUsersPageView>(
            admin,
            "/api/admin/users?status=all&online=all&limit=2");
        firstPage.Items.Should().HaveCount(2);
        firstPage.NextCursor.Should().NotBeNullOrWhiteSpace();
        var secondPage = await GetAsync<AdminUsersPageView>(
            admin,
            $"/api/admin/users?status=all&online=all&limit=2&cursor={Uri.EscapeDataString(firstPage.NextCursor!)}");
        firstPage.Items.Select(item => item.PlayerId)
            .Should().NotIntersectWith(secondPage.Items.Select(item => item.PlayerId));
        var pagedPlayerIds = firstPage.Items.Concat(secondPage.Items)
            .Select(item => item.PlayerId)
            .ToArray();
        pagedPlayerIds.Should().HaveCount(4);
        pagedPlayerIds.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Admin_detail_history_and_replay_are_authorized_and_never_return_token_hashes()
    {
        using var player = await CreatePlayerAsync();
        using var opponent = await CreatePlayerAsync();
        using var unrelated = await CreatePlayerAsync();
        var gameId = await CreateFinishedPrivateGameAsync(player.Client, opponent.Client);
        using var admin = await CreateAdminAsync();

        var detailResponse = await admin.GetAsync($"/api/admin/users/{player.Session.PlayerId:D}");
        detailResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var detailBody = await detailResponse.Content.ReadAsStringAsync();
        var detail = Deserialize<AdminUserDetailView>(detailBody);
        detail.User.PlayerId.Should().Be(player.Session.PlayerId);

        var historyResponse = await admin.GetAsync($"/api/admin/users/{player.Session.PlayerId:D}/games?limit=20");
        historyResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var historyBody = await historyResponse.Content.ReadAsStringAsync();
        var history = Deserialize<AdminHistoricalGamesPageView>(historyBody);
        history.Games.Should().ContainSingle(game => game.GameId == gameId);
        history.Games.Should().OnlyContain(game => game.IsRated == false);

        var unrelatedHistory = await GetAsync<AdminHistoricalGamesPageView>(
            admin,
            $"/api/admin/users/{unrelated.Session.PlayerId:D}/games?limit=20");
        unrelatedHistory.Games.Should().BeEmpty();

        var replayResponse = await admin.GetAsync($"/api/admin/games/{gameId:D}/replay");
        replayResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var replayBody = await replayResponse.Content.ReadAsStringAsync();
        var replay = Deserialize<HistoricalReplayView>(replayBody);
        replay.GameId.Should().Be(gameId);
        replay.CurrentPlayerSide.Should().BeNull();
        replay.Frames.Should().NotBeEmpty();
        replay.Frames.Should().OnlyContain(frame =>
            frame.Views.Red != null &&
            frame.Views.Black != null &&
            frame.Views.Omniscient != null);

        var serializedResponses = string.Join("\n", detailBody, historyBody, replayBody);
        serializedResponses.Contains("tokenHash", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        serializedResponses.Contains("passwordHash", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }

    [Fact]
    public async Task Repeated_private_game_ban_and_unban_are_idempotent_without_restoring_the_game_or_ratings()
    {
        using var player = await CreatePlayerAsync();
        using var opponent = await CreatePlayerAsync();
        var gameId = await CreatePlayingPrivateGameAsync(player.Client, opponent.Client);
        var playerView = await GetAsync<MistChess.Api.Contracts.GameView>(
            player.Client,
            $"/api/games/{gameId:D}");
        var expectedWinner = playerView.Perspective == Side.Red ? Side.Black : Side.Red;
        using var admin = await CreateAdminAsync();

        admin.DefaultRequestHeaders.Remove("X-CSRF-TOKEN").Should().BeTrue();
        var missingAntiforgery = await admin.PostAsJsonAsync(
            $"/api/admin/users/{player.Session.PlayerId:D}/ban",
            new AdminBanRequest("private game moderation"),
            JsonOptions);
        missingAntiforgery.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AddAdminAntiforgeryTokenAsync(admin);

        var first = await PostAsync<AdminBanStatusView>(
            admin,
            $"/api/admin/users/{player.Session.PlayerId:D}/ban",
            new AdminBanRequest("private game moderation"));
        var repeated = await PostAsync<AdminBanStatusView>(
            admin,
            $"/api/admin/users/{player.Session.PlayerId:D}/ban",
            new AdminBanRequest("a replacement reason must not rerun moderation"));
        repeated.Should().BeEquivalentTo(first);

        var afterBan = await GetModerationStateAsync(gameId, player.Session.PlayerId, opponent.Session.PlayerId);
        afterBan.Status.Should().Be(GameStatus.Finished.ToString());
        afterBan.Winner.Should().Be(expectedWinner.ToString());
        afterBan.ResultReason.Should().Be(GameResultReason.AdministrativeForfeit.ToString());
        afterBan.IsRated.Should().BeFalse();
        afterBan.RatingSettlements.Should().Be(0);
        afterBan.RatingRows.Should().Be(0);

        var unbanResponse = await admin.DeleteAsync($"/api/admin/users/{player.Session.PlayerId:D}/ban");
        unbanResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var unbanned = await ReadAsync<AdminBanStatusView>(unbanResponse);
        unbanned.Banned.Should().BeFalse();
        var repeatedUnbanResponse = await admin.DeleteAsync($"/api/admin/users/{player.Session.PlayerId:D}/ban");
        repeatedUnbanResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync<AdminBanStatusView>(repeatedUnbanResponse)).Should().BeEquivalentTo(unbanned);

        var afterUnban = await GetModerationStateAsync(gameId, player.Session.PlayerId, opponent.Session.PlayerId);
        afterUnban.Should().Be(afterBan);
        var bootstrap = await player.Client.PostAsync("/api/sessions/guest", null);
        bootstrap.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync<GuestSessionView>(bootstrap)).ActiveGameId.Should().BeNull();
    }

    [Fact]
    public async Task Banning_a_player_in_a_rated_game_forfeits_and_settles_exactly_once()
    {
        using var player = await CreatePlayerAsync();
        using var opponent = await CreatePlayerAsync();
        await PostAsync<MatchTicketView>(
            player.Client,
            "/api/matchmaking/tickets",
            new CreateMatchTicketRequest(GameState.CurrentRuleVersion, "ban-rated-player"));
        var matched = await PostAsync<MatchTicketView>(
            opponent.Client,
            "/api/matchmaking/tickets",
            new CreateMatchTicketRequest(GameState.CurrentRuleVersion, "ban-rated-opponent"));
        matched.GameId.Should().NotBeNull();
        var gameId = matched.GameId!.Value;
        var playerView = await GetAsync<MistChess.Api.Contracts.GameView>(
            player.Client,
            $"/api/games/{gameId:D}");
        var expectedWinner = playerView.Perspective == Side.Red ? Side.Black : Side.Red;
        using var admin = await CreateAdminAsync();

        await PostAsync<AdminBanStatusView>(
            admin,
            $"/api/admin/users/{player.Session.PlayerId:D}/ban",
            new AdminBanRequest("rated game moderation"));
        await PostAsync<AdminBanStatusView>(
            admin,
            $"/api/admin/users/{player.Session.PlayerId:D}/ban",
            new AdminBanRequest("rated game moderation"));

        var state = await GetModerationStateAsync(gameId, player.Session.PlayerId, opponent.Session.PlayerId);
        state.Status.Should().Be(GameStatus.Finished.ToString());
        state.Winner.Should().Be(expectedWinner.ToString());
        state.ResultReason.Should().Be(GameResultReason.AdministrativeForfeit.ToString());
        state.IsRated.Should().BeTrue();
        state.RatingSettlements.Should().Be(1);
        state.RatingRows.Should().Be(2);

        var ratings = await GetPrimaryRatingsAsync(player.Session.PlayerId, opponent.Session.PlayerId);
        ratings[player.Session.PlayerId].Rating.Should().Be(1480);
        ratings[player.Session.PlayerId].GamesPlayed.Should().Be(1);
        ratings[player.Session.PlayerId].Losses.Should().Be(1);
        ratings[opponent.Session.PlayerId].Rating.Should().Be(1520);
        ratings[opponent.Session.PlayerId].GamesPlayed.Should().Be(1);
        ratings[opponent.Session.PlayerId].Wins.Should().Be(1);
    }

    [Fact]
    public async Task Banning_a_searching_player_cancels_the_ticket_and_unban_does_not_restore_it()
    {
        using var player = await CreatePlayerAsync();
        var searching = await PostAsync<MatchTicketView>(
            player.Client,
            "/api/matchmaking/tickets",
            new CreateMatchTicketRequest(GameState.CurrentRuleVersion, "search-before-ban"));
        searching.Status.Should().Be(MatchTicketStatus.Searching);
        using var admin = await CreateAdminAsync();

        await PostAsync<AdminBanStatusView>(
            admin,
            $"/api/admin/users/{player.Session.PlayerId:D}/ban",
            new AdminBanRequest("cancel active matchmaking"));
        var unban = await admin.DeleteAsync($"/api/admin/users/{player.Session.PlayerId:D}/ban");
        unban.StatusCode.Should().Be(HttpStatusCode.OK);

        var cancelled = await GetAsync<MatchTicketView>(
            player.Client,
            "/api/matchmaking/tickets/current");
        cancelled.TicketId.Should().Be(searching.TicketId);
        cancelled.Status.Should().Be(MatchTicketStatus.Cancelled);
        cancelled.GameId.Should().BeNull();

        var replacement = await PostAsync<MatchTicketView>(
            player.Client,
            "/api/matchmaking/tickets",
            new CreateMatchTicketRequest(GameState.CurrentRuleVersion, "search-after-unban"));
        replacement.TicketId.Should().NotBe(searching.TicketId);
        replacement.Status.Should().Be(MatchTicketStatus.Searching);
    }

    [Fact]
    public async Task Banning_a_waiting_room_host_closes_the_room_for_the_remaining_member()
    {
        using var host = await CreatePlayerAsync();
        using var member = await CreatePlayerAsync();
        var room = await PostAsync<RoomView>(
            host.Client,
            "/api/rooms",
            new CreateRoomRequest(GameState.CurrentRuleVersion, null));
        var joined = await PostAsync<RoomView>(
            member.Client,
            $"/api/rooms/{room.Code}/join",
            new { });
        joined.Status.Should().Be(GameStatus.WaitingForReady);
        using var admin = await CreateAdminAsync();

        await PostAsync<AdminBanStatusView>(
            admin,
            $"/api/admin/users/{host.Session.PlayerId:D}/ban",
            new AdminBanRequest("close hosted waiting room"));

        using var ready = await member.Client.PostAsJsonAsync(
            $"/api/rooms/{room.Code}/ready",
            new SetReadyRequest(true),
            JsonOptions);
        ready.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadAsync<ErrorResponse>(ready)).Code.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task Banning_a_waiting_room_member_removes_them_and_resets_the_host_ready_state()
    {
        using var host = await CreatePlayerAsync();
        using var member = await CreatePlayerAsync();
        using var replacement = await CreatePlayerAsync();
        var room = await PostAsync<RoomView>(
            host.Client,
            "/api/rooms",
            new CreateRoomRequest(GameState.CurrentRuleVersion, null));
        await PostAsync<RoomView>(
            member.Client,
            $"/api/rooms/{room.Code}/join",
            new { });
        var hostReady = await PostAsync<RoomView>(
            host.Client,
            $"/api/rooms/{room.Code}/ready",
            new SetReadyRequest(true));
        hostReady.Players.Single(player => player.IsCurrentPlayer).IsReady.Should().BeTrue();
        using var admin = await CreateAdminAsync();

        await PostAsync<AdminBanStatusView>(
            admin,
            $"/api/admin/users/{member.Session.PlayerId:D}/ban",
            new AdminBanRequest("remove waiting room member"));

        var rejoined = await PostAsync<RoomView>(
            replacement.Client,
            $"/api/rooms/{room.Code}/join",
            new { });
        rejoined.Status.Should().Be(GameStatus.WaitingForReady);
        rejoined.Players.Should().HaveCount(2);
        rejoined.Players.Should().ContainSingle(player => player.IsCurrentPlayer);
        rejoined.Players.Should().OnlyContain(player => !player.IsReady);
    }

    [Fact]
    public async Task Banning_the_other_room_member_does_not_report_the_caller_as_banned()
    {
        using var host = await CreatePlayerAsync();
        using var member = await CreatePlayerAsync();
        var room = await PostAsync<RoomView>(
            host.Client,
            "/api/rooms",
            new CreateRoomRequest(GameState.CurrentRuleVersion, null));
        await PostAsync<RoomView>(
            member.Client,
            $"/api/rooms/{room.Code}/join",
            new { });
        using var admin = await CreateAdminAsync();
        var orderedPlayers = new[] { host, member }
            .OrderBy(player => player.Session.PlayerId.ToString("D"), StringComparer.Ordinal)
            .ToArray();
        var caller = orderedPlayers[0];
        var bannedOpponent = orderedPlayers[1];

        await using var lockConnection = new NpgsqlConnection(database.ConnectionString);
        await lockConnection.OpenAsync();
        await using var lockTransaction = await lockConnection.BeginTransactionAsync();
        await using (var lockCommand = new NpgsqlCommand(
            "SELECT 1 FROM guest_sessions WHERE id = @player_id FOR UPDATE",
            lockConnection,
            lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("player_id", caller.Session.PlayerId);
            (await lockCommand.ExecuteScalarAsync()).Should().Be(1);
        }

        var readyTask = caller.Client.PostAsJsonAsync(
            $"/api/rooms/{room.Code}/ready",
            new SetReadyRequest(true),
            JsonOptions);
        await using var monitorConnection = new NpgsqlConnection(database.ConnectionString);
        await monitorConnection.OpenAsync();
        var blocked = false;
        await using (var monitorCommand = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM pg_stat_activity
                WHERE datname = current_database()
                  AND pid <> pg_backend_pid()
                  AND cardinality(pg_blocking_pids(pid)) > 0)
            """,
            monitorConnection))
        {
            for (var attempt = 0; attempt < 200 && !blocked; attempt++)
            {
                blocked = (bool)(await monitorCommand.ExecuteScalarAsync()
                    ?? throw new InvalidOperationException("The lock monitor returned no value."));
                if (!blocked)
                {
                    await Task.Delay(5);
                }
            }
        }
        blocked.Should().BeTrue();

        var ban = await admin.PostAsJsonAsync(
            $"/api/admin/users/{bannedOpponent.Session.PlayerId:D}/ban",
            new AdminBanRequest("room ready race"),
            JsonOptions);
        ban.StatusCode.Should().Be(HttpStatusCode.OK);
        await lockTransaction.CommitAsync();

        using var ready = await readyTask;
        ready.StatusCode.Should().BeOneOf(HttpStatusCode.Conflict, HttpStatusCode.NotFound);
        (await ReadAsync<ErrorResponse>(ready)).Code.Should().NotBe("PLAYER_BANNED");
    }

    [Fact]
    public async Task Concurrent_ban_and_opponent_resignation_finish_and_settle_the_rated_game_exactly_once()
    {
        using var player = await CreatePlayerAsync();
        using var opponent = await CreatePlayerAsync();
        await PostAsync<MatchTicketView>(
            player.Client,
            "/api/matchmaking/tickets",
            new CreateMatchTicketRequest(GameState.CurrentRuleVersion, "concurrent-ban-player"));
        var matched = await PostAsync<MatchTicketView>(
            opponent.Client,
            "/api/matchmaking/tickets",
            new CreateMatchTicketRequest(GameState.CurrentRuleVersion, "concurrent-ban-opponent"));
        matched.GameId.Should().NotBeNull();
        var gameId = matched.GameId!.Value;
        var playerView = await GetAsync<MistChess.Api.Contracts.GameView>(
            player.Client,
            $"/api/games/{gameId:D}");
        var administrativeWinner = playerView.Perspective == Side.Red ? Side.Black : Side.Red;
        using var admin = await CreateAdminAsync();
        var startGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<HttpResponseMessage> BanAsync()
        {
            await startGate.Task;
            return await admin.PostAsJsonAsync(
                $"/api/admin/users/{player.Session.PlayerId:D}/ban",
                new AdminBanRequest("concurrent terminal action"),
                JsonOptions);
        }

        async Task<HttpResponseMessage> ResignAsync()
        {
            await startGate.Task;
            return await opponent.Client.PostAsJsonAsync(
                $"/api/games/{gameId:D}/resign",
                new { },
                JsonOptions);
        }

        var banTask = BanAsync();
        var resignTask = ResignAsync();
        startGate.SetResult(true);
        await Task.WhenAll(banTask, resignTask);
        using var banResponse = await banTask;
        using var resignResponse = await resignTask;
        banResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        resignResponse.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Conflict);

        var state = await GetModerationStateAsync(
            gameId,
            player.Session.PlayerId,
            opponent.Session.PlayerId);
        state.Status.Should().Be(GameStatus.Finished.ToString());
        state.ResultReason.Should().BeOneOf(
            GameResultReason.AdministrativeForfeit.ToString(),
            GameResultReason.Resignation.ToString());
        var expectedWinner = state.ResultReason == GameResultReason.AdministrativeForfeit.ToString()
            ? administrativeWinner
            : playerView.Perspective;
        state.Winner.Should().Be(expectedWinner.ToString());
        state.IsRated.Should().BeTrue();
        state.RatingSettlements.Should().Be(1);
        state.RatingRows.Should().Be(2);

        var ratings = await GetPrimaryRatingsAsync(
            player.Session.PlayerId,
            opponent.Session.PlayerId);
        ratings.Values.Sum(rating => rating.GamesPlayed).Should().Be(2);
        ratings.Values.Sum(rating => rating.Wins).Should().Be(1);
        ratings.Values.Sum(rating => rating.Losses).Should().Be(1);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static IReadOnlyDictionary<string, string?> CreateAdminSettings()
    {
        var options = new AdminOptions { Username = AdminUsername };
        options.PasswordHash = new PasswordHasher<AdminOptions>().HashPassword(options, AdminPassword);
        return new Dictionary<string, string?>
        {
            ["Admin:Username"] = options.Username,
            ["Admin:PasswordHash"] = options.PasswordHash
        };
    }

    private async Task<PlayerClient> CreatePlayerAsync()
    {
        var client = _factory.CreateHttpsClient();
        client.DefaultRequestHeaders.Add("X-Requested-With", "MistChess");
        var sessionResponse = await client.PostAsync("/api/sessions/guest", null);
        sessionResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var session = await ReadAsync<GuestSessionView>(sessionResponse);
        var antiforgeryResponse = await client.GetAsync("/api/antiforgery/token");
        antiforgeryResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = await ReadAsync<AntiforgeryTokenView>(antiforgeryResponse);
        client.DefaultRequestHeaders.Add(token.HeaderName, token.Token);
        return new PlayerClient(client, session);
    }

    private async Task<HttpClient> CreateAdminAsync()
    {
        var client = _factory.CreateHttpsClient();
        var loginToken = await AddAdminAntiforgeryTokenAsync(client);
        var login = await client.PostAsJsonAsync(
            "/api/admin/session",
            new AdminLoginRequest(AdminUsername, AdminPassword),
            JsonOptions);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        client.DefaultRequestHeaders.Remove(loginToken.HeaderName).Should().BeTrue();
        await AddAdminAntiforgeryTokenAsync(client);
        return client;
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await ReadAsync<T>(response);
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string path, object body)
    {
        var response = await client.PostAsJsonAsync(path, body, JsonOptions);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await ReadAsync<T>(response);
    }

    private static async Task<Guid> CreatePlayingPrivateGameAsync(HttpClient first, HttpClient second)
    {
        var room = await PostAsync<RoomView>(
            first,
            "/api/rooms",
            new CreateRoomRequest(GameState.CurrentRuleVersion, null));
        await PostAsync<RoomView>(second, $"/api/rooms/{room.Code}/join", new { });
        await PostAsync<RoomView>(
            first,
            $"/api/rooms/{room.Code}/ready",
            new SetReadyRequest(true));
        var started = await PostAsync<RoomView>(
            second,
            $"/api/rooms/{room.Code}/ready",
            new SetReadyRequest(true));
        started.GameId.Should().NotBeNull();
        return started.GameId!.Value;
    }

    private static async Task<Guid> CreateFinishedPrivateGameAsync(HttpClient first, HttpClient second)
    {
        var gameId = await CreatePlayingPrivateGameAsync(first, second);
        await PostAsync<MistChess.Api.Contracts.GameView>(
            first,
            $"/api/games/{gameId:D}/resign",
            new { });
        return gameId;
    }

    private static async Task<AntiforgeryTokenView> AddAdminAntiforgeryTokenAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/admin/antiforgery/token");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = await ReadAsync<AntiforgeryTokenView>(response);
        client.DefaultRequestHeaders.Add(token.HeaderName, token.Token);
        return token;
    }

    private async Task SetProfileAsync(
        Guid playerId,
        string displayName,
        DateTimeOffset lastSeenAt,
        bool banned = false)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE guest_sessions
            SET display_name = @display_name,
                last_seen_at = @last_seen_at,
                is_banned = @banned,
                banned_at = CASE WHEN @banned THEN @banned_at ELSE NULL END,
                ban_reason = CASE WHEN @banned THEN 'seeded moderation' ELSE NULL END,
                banned_by = CASE WHEN @banned THEN @banned_by ELSE NULL END
            WHERE id = @id
            """,
            connection);
        command.Parameters.AddWithValue("display_name", displayName);
        command.Parameters.AddWithValue("last_seen_at", lastSeenAt);
        command.Parameters.AddWithValue("banned", banned);
        command.Parameters.AddWithValue("banned_at", InitialTime);
        command.Parameters.AddWithValue("banned_by", AdminUsername);
        command.Parameters.AddWithValue("id", playerId);
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    private async Task SetPrimaryRatingAsync(
        Guid playerId,
        int rating,
        int gamesPlayed,
        int wins,
        int draws,
        int losses)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO player_ratings
                (player_id, rule_version, time_control, rating, games_played,
                 wins, draws, losses, updated_at, concurrency_stamp)
            VALUES
                (@player_id, @rule_version, '600+5', @rating, @games_played,
                 @wins, @draws, @losses, @updated_at, 0)
            """,
            connection);
        command.Parameters.AddWithValue("player_id", playerId);
        command.Parameters.AddWithValue("rule_version", GameState.CurrentRuleVersion);
        command.Parameters.AddWithValue("rating", rating);
        command.Parameters.AddWithValue("games_played", gamesPlayed);
        command.Parameters.AddWithValue("wins", wins);
        command.Parameters.AddWithValue("draws", draws);
        command.Parameters.AddWithValue("losses", losses);
        command.Parameters.AddWithValue("updated_at", InitialTime);
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    private async Task<ModerationState> GetModerationStateAsync(
        Guid gameId,
        Guid firstPlayerId,
        Guid secondPlayerId)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT status,
                   winner,
                   result_reason,
                   is_rated,
                   (SELECT count(*) FROM rating_settlements WHERE game_id = @game_id),
                   (SELECT count(*) FROM player_ratings
                    WHERE player_id = @first_player_id OR player_id = @second_player_id)
            FROM games
            WHERE id = @game_id
            """,
            connection);
        command.Parameters.AddWithValue("game_id", gameId);
        command.Parameters.AddWithValue("first_player_id", firstPlayerId);
        command.Parameters.AddWithValue("second_player_id", secondPlayerId);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return new ModerationState(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetBoolean(3),
            reader.GetInt64(4),
            reader.GetInt64(5));
    }

    private async Task<IReadOnlyDictionary<Guid, RatingRow>> GetPrimaryRatingsAsync(
        Guid firstPlayerId,
        Guid secondPlayerId)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT player_id, rating, games_played, wins, losses
            FROM player_ratings
            WHERE rule_version = @rule_version
              AND time_control = '600+5'
              AND (player_id = @first_player_id OR player_id = @second_player_id)
            """,
            connection);
        command.Parameters.AddWithValue("rule_version", GameState.CurrentRuleVersion);
        command.Parameters.AddWithValue("first_player_id", firstPlayerId);
        command.Parameters.AddWithValue("second_player_id", secondPlayerId);
        await using var reader = await command.ExecuteReaderAsync();
        var ratings = new Dictionary<Guid, RatingRow>();
        while (await reader.ReadAsync())
        {
            ratings.Add(
                reader.GetGuid(0),
                new RatingRow(
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4)));
        }

        return ratings;
    }

    private async Task SetLastSeenAtAsync(Guid playerId, DateTimeOffset value)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE guest_sessions SET last_seen_at = @last_seen_at WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("last_seen_at", value);
        command.Parameters.AddWithValue("id", playerId);
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    private async Task<DateTimeOffset> GetLastSeenAtAsync(Guid playerId)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT last_seen_at FROM guest_sessions WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("id", playerId);
        return (await command.ExecuteScalarAsync()) switch
        {
            DateTimeOffset value => value,
            DateTime value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
            _ => throw new InvalidOperationException("The guest session was not found.")
        };
    }

    private async Task BanDirectlyAsync(Guid playerId, string reason)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE guest_sessions
            SET is_banned = TRUE,
                banned_at = @banned_at,
                ban_reason = @reason,
                banned_by = @banned_by
            WHERE id = @id
            """,
            connection);
        command.Parameters.AddWithValue("banned_at", _clock.GetUtcNow());
        command.Parameters.AddWithValue("reason", reason);
        command.Parameters.AddWithValue("banned_by", AdminUsername);
        command.Parameters.AddWithValue("id", playerId);
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    private async Task<long> CountGuestSessionsAsync()
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM guest_sessions", connection);
        return (long)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("PostgreSQL did not return a session count."));
    }

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidOperationException("The API response body was empty.");

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException("The API response body was empty.");

    private sealed record ModerationState(
        string Status,
        string Winner,
        string ResultReason,
        bool IsRated,
        long RatingSettlements,
        long RatingRows);

    private sealed record RatingRow(int Rating, int GamesPlayed, int Wins, int Losses);

    private sealed record PlayerClient(HttpClient Client, GuestSessionView Session) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow = _utcNow.Add(amount);
    }
}
