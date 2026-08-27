using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Profiles;

public interface IProfileRepository
{
    ValueTask<IReadOnlyList<ServerProfile>> ListAsync(CancellationToken cancellationToken = default);

    ValueTask<ServerProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    ValueTask UpsertAsync(ServerProfile profile, CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
