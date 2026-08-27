using ServerDesk.Domain.Secrets;

namespace ServerDesk.Application.Secrets;

public interface ISecretStore
{
    ValueTask SetAsync(
        SecretReference reference,
        string secret,
        CancellationToken cancellationToken = default);

    ValueTask<string?> GetAsync(
        SecretReference reference,
        CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(
        SecretReference reference,
        CancellationToken cancellationToken = default);
}
