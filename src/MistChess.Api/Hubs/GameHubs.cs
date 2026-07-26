using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MistChess.Api.Application;
using MistChess.Api.Contracts;
using MistChess.Api.Security;
using MistChess.Infrastructure.Persistence;

namespace MistChess.Api.Hubs;

public static class HubGroups
{
    public static string LobbyPlayer(Guid playerId) => $"player:{playerId:N}";
    public static string GamePlayer(Guid gameId, Guid playerId) => $"game:{gameId:N}:player:{playerId:N}";
}

[Authorize]
public sealed class LobbyHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var playerId = CurrentPlayer.GetId(Context.User!);
        await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.LobbyPlayer(playerId));
        await base.OnConnectedAsync();
    }
}

[Authorize]
public sealed class GameHub(
    IDbContextFactory<MistChessDbContext> contextFactory,
    GameViewProjector projector,
    GameConnectionTracker connections) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var playerId = CurrentPlayer.GetId(Context.User!);
        var httpContext = Context.GetHttpContext() ?? throw new HubException("NOT_FOUND");
        if (!Guid.TryParse(httpContext.Request.Query["gameId"], out var gameId))
        {
            throw new HubException("NOT_FOUND");
        }

        await using var db = await contextFactory.CreateDbContextAsync(Context.ConnectionAborted);
        var game = await db.Games.AsNoTracking().SingleOrDefaultAsync(
            value => value.Id == gameId && (value.RedPlayerId == playerId || value.BlackPlayerId == playerId),
            Context.ConnectionAborted);
        if (game is null)
        {
            throw new HubException("NOT_FOUND");
        }

        var drawOffer = await db.DrawOffers.AsNoTracking().SingleOrDefaultAsync(
            value => value.GameId == game.Id &&
                value.Status == MistChess.Infrastructure.Persistence.DrawOfferStatus.Pending,
            Context.ConnectionAborted);

        Context.Items[nameof(GameEntity.Id)] = game.Id;
        Context.Items[nameof(CurrentPlayer)] = playerId;
        await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.GamePlayer(game.Id, playerId));
        var opponentId = game.RedPlayerId == playerId ? game.BlackPlayerId : game.RedPlayerId;
        var firstConnection = connections.Connect(game.Id, playerId, Context.ConnectionId);
        await Clients.Caller
            .SendAsync(
                "OpponentConnectionChanged",
                new ConnectionState(connections.IsConnected(game.Id, opponentId)),
                Context.ConnectionAborted);
        if (firstConnection)
        {
            await Clients.Group(HubGroups.GamePlayer(game.Id, opponentId))
                .SendAsync("OpponentConnectionChanged", new ConnectionState(true), Context.ConnectionAborted);
        }

        var suppliedVersion = long.TryParse(httpContext.Request.Query["version"], out var version) ? version : -1;
        if (game.Version > suppliedVersion)
        {
            await Clients.Caller.SendAsync(
                "GameViewUpdated",
                projector.Project(
                    game,
                    playerId,
                    drawOffer is null ? null : GameViewProjector.MapDrawOffer(game, drawOffer)),
                Context.ConnectionAborted);
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue(nameof(GameEntity.Id), out var gameValue) &&
            gameValue is Guid gameId &&
            Context.Items.TryGetValue(nameof(CurrentPlayer), out var playerValue) &&
            playerValue is Guid playerId)
        {
            var lastConnection = connections.Disconnect(gameId, playerId, Context.ConnectionId);
            if (lastConnection)
            {
                await using var db = await contextFactory.CreateDbContextAsync();
                var game = await db.Games.AsNoTracking().SingleOrDefaultAsync(value => value.Id == gameId);
                if (game is not null)
                {
                    var opponentId = game.RedPlayerId == playerId ? game.BlackPlayerId : game.RedPlayerId;
                    await Clients.Group(HubGroups.GamePlayer(game.Id, opponentId))
                        .SendAsync("OpponentConnectionChanged", new ConnectionState(false));
                }
            }
        }

        await base.OnDisconnectedAsync(exception);
    }
}

public sealed class GameConnectionTracker
{
    private readonly ConcurrentDictionary<(Guid GameId, Guid PlayerId), ConcurrentDictionary<string, byte>> _connections = new();

    public bool Connect(Guid gameId, Guid playerId, string connectionId)
    {
        var key = (gameId, playerId);
        while (true)
        {
            var group = _connections.GetOrAdd(key, static _ => new ConcurrentDictionary<string, byte>());
            lock (group)
            {
                if (!_connections.TryGetValue(key, out var current) || !ReferenceEquals(current, group))
                {
                    continue;
                }

                group[connectionId] = 0;
                return group.Count == 1;
            }
        }
    }

    public bool IsConnected(Guid gameId, Guid playerId)
    {
        var key = (gameId, playerId);
        while (_connections.TryGetValue(key, out var group))
        {
            lock (group)
            {
                if (!_connections.TryGetValue(key, out var current) || !ReferenceEquals(current, group))
                {
                    continue;
                }

                return !group.IsEmpty;
            }
        }

        return false;
    }

    public bool Disconnect(Guid gameId, Guid playerId, string connectionId)
    {
        var key = (gameId, playerId);
        if (!_connections.TryGetValue(key, out var group))
        {
            return false;
        }

        lock (group)
        {
            group.TryRemove(connectionId, out _);
            if (!group.IsEmpty)
            {
                return false;
            }

            ((ICollection<KeyValuePair<(Guid GameId, Guid PlayerId), ConcurrentDictionary<string, byte>>>)_connections)
                .Remove(new KeyValuePair<(Guid GameId, Guid PlayerId), ConcurrentDictionary<string, byte>>(key, group));
            return true;
        }
    }
}

public sealed class SignalRLobbyNotifier(IHubContext<LobbyHub> hubContext) : ILobbyNotifier
{
    public Task TicketUpdatedAsync(Guid playerId, MatchTicketView ticket, CancellationToken cancellationToken) =>
        hubContext.Clients.Group(HubGroups.LobbyPlayer(playerId))
            .SendAsync("MatchTicketUpdated", ticket, cancellationToken);

    public Task MatchFoundAsync(Guid playerId, MatchFoundView match, CancellationToken cancellationToken) =>
        hubContext.Clients.Group(HubGroups.LobbyPlayer(playerId))
            .SendAsync("MatchFound", match, cancellationToken);
}

public sealed class SignalRGameNotifier(
    IHubContext<GameHub> hubContext,
    IDbContextFactory<MistChessDbContext> contextFactory,
    GameViewProjector projector) : IGameNotifier
{
    public async Task GameUpdatedAsync(Guid gameId, bool ended, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var game = await db.Games.AsNoTracking().SingleAsync(value => value.Id == gameId, cancellationToken);
        var drawOffer = await db.DrawOffers.AsNoTracking().SingleOrDefaultAsync(
            value => value.GameId == game.Id &&
                value.Status == MistChess.Infrastructure.Persistence.DrawOfferStatus.Pending,
            cancellationToken);
        var eventName = ended ? "GameEnded" : "GameViewUpdated";
        await Task.WhenAll(
            hubContext.Clients.Group(HubGroups.GamePlayer(game.Id, game.RedPlayerId))
                .SendAsync(
                    eventName,
                    projector.Project(
                        game,
                        game.RedPlayerId,
                        drawOffer is null ? null : GameViewProjector.MapDrawOffer(game, drawOffer)),
                    cancellationToken),
            hubContext.Clients.Group(HubGroups.GamePlayer(game.Id, game.BlackPlayerId))
                .SendAsync(
                    eventName,
                    projector.Project(
                        game,
                        game.BlackPlayerId,
                        drawOffer is null ? null : GameViewProjector.MapDrawOffer(game, drawOffer)),
                    cancellationToken));
    }

    public async Task DrawOfferChangedAsync(Guid gameId, DrawOfferView offer, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var game = await db.Games.AsNoTracking().SingleAsync(value => value.Id == gameId, cancellationToken);
        await Task.WhenAll(
            hubContext.Clients.Group(HubGroups.GamePlayer(game.Id, game.RedPlayerId))
                .SendAsync("DrawOfferChanged", offer, cancellationToken),
            hubContext.Clients.Group(HubGroups.GamePlayer(game.Id, game.BlackPlayerId))
                .SendAsync("DrawOfferChanged", offer, cancellationToken));
    }
}
