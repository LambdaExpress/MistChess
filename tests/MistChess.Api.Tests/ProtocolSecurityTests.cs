using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using MistChess.Api.Contracts;
using MistChess.Api.Tests.Infrastructure;

namespace MistChess.Api.Tests;

[Trait("Category", "Security")]
public sealed class ProtocolSecurityTests : IDisposable
{
    private readonly MistChessWebApplicationFactory _factory = MistChessWebApplicationFactory.WithoutDatabase();

    [Fact]
    public async Task Protected_resources_reject_requests_without_a_guest_session()
    {
        using var client = _factory.CreateHttpsClient();
        var gameId = Guid.NewGuid();

        var snapshot = await client.GetAsync($"/api/games/{gameId:D}");
        var replay = await client.GetAsync($"/api/games/{gameId:D}/replay");
        var ticket = await client.GetAsync("/api/matchmaking/tickets/current");

        snapshot.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        ticket.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task State_changing_request_requires_an_antiforgery_token_before_application_code_runs()
    {
        using var authenticatedFactory = MistChessWebApplicationFactory.WithoutDatabase(authenticated: true);
        using var client = authenticatedFactory.CreateHttpsClient();

        var response = await client.PostAsJsonAsync(
            "/api/rooms",
            new CreateRoomRequest("fog-xiangqi-v1", null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task OpenApi_contract_exposes_only_safe_player_protocols()
    {
        using var client = _factory.CreateHttpsClient();

        var response = await client.GetAsync("/openapi/v1.json");
        var document = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        document.Should().Contain("/api/games/{gameId}");
        document.Should().Contain("clientMoveId");
        document.Should().Contain("expectedVersion");
        document.Should().NotContain("GameState");
        document.Should().NotContain("checkedSide");
        document.Should().NotContain("isInCheck");
        document.Should().NotContain("generalThreatened");
        document.Should().NotContain("eligiblePopulation");
        document.Should().NotContain("populationBand");
        document.Should().NotContain("populationBaseRadius");
        document.Should().NotContain("waitingBonus");
        document.Should().NotContain("effectiveRadius");
        document.Should().NotContain("ratingSnapshot");
        document.Should().NotContain("tokenHash");
        document.Should().NotContain("ownerPlayerId");
    }

    [Fact]
    public async Task Missing_position_members_are_rejected_before_game_lookup()
    {
        using var authenticatedFactory = MistChessWebApplicationFactory.WithoutDatabase(authenticated: true);
        using var client = authenticatedFactory.CreateHttpsClient();
        var token = await client.GetFromJsonAsync<AntiforgeryTokenView>("/api/antiforgery/token")
            ?? throw new InvalidOperationException("The antiforgery response was empty.");
        client.DefaultRequestHeaders.Add(token.HeaderName, token.Token);

        var response = await client.PostAsJsonAsync(
            $"/api/games/{Guid.NewGuid():D}/moves",
            new
            {
                from = new { rank = 0 },
                to = new { file = 0, rank = 1 },
                expectedVersion = 0,
                clientMoveId = "missing-file"
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Code.Should().Be("INVALID_REQUEST");
    }

    [Fact]
    public async Task Cross_origin_websocket_upgrade_is_rejected()
    {
        using var authenticatedFactory = MistChessWebApplicationFactory.WithoutDatabase(authenticated: true);
        var webSocketClient = authenticatedFactory.Server.CreateWebSocketClient();
        webSocketClient.ConfigureRequest = request =>
            request.Headers["Origin"] = "https://untrusted.example";

        Func<Task> connect = async () =>
        {
            using var socket = await webSocketClient.ConnectAsync(
                new Uri("ws://localhost/hubs/lobby"),
                CancellationToken.None);
        };

        var exception = await connect.Should().ThrowAsync<Exception>();
        exception.Which.Message.Should().Contain("403");
    }

    [Fact]
    public async Task Preauthentication_rate_limit_covers_non_api_dynamic_endpoints()
    {
        using var isolatedFactory = MistChessWebApplicationFactory.WithoutDatabase();
        using var client = isolatedFactory.CreateHttpsClient();

        for (var requestNumber = 0; requestNumber < 120; requestNumber++)
        {
            (await client.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var limited = await client.GetAsync("/health/live");
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await limited.Content.ReadFromJsonAsync<ErrorResponse>())!.Code.Should().Be("RATE_LIMITED");
    }

    public void Dispose() => _factory.Dispose();
}
