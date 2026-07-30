using MistChess.Api.Contracts;

namespace MistChess.Api.Application;

public interface ILobbyNotifier
{
    Task TicketUpdatedAsync(Guid playerId, MatchTicketView ticket, CancellationToken cancellationToken);
    Task MatchFoundAsync(Guid playerId, MatchFoundView match, CancellationToken cancellationToken);
}

public interface IGameNotifier
{
    Task GameUpdatedAsync(Guid gameId, bool ended, CancellationToken cancellationToken);
    Task DrawOfferChangedAsync(Guid gameId, DrawOfferView offer, CancellationToken cancellationToken);
}

public interface IAccountNotifier
{
    Task AccountBannedAsync(
        Guid playerId,
        Guid? gameId,
        string reason,
        CancellationToken cancellationToken);
}
