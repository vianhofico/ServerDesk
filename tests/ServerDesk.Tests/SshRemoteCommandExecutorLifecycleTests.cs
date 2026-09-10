using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Remote;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Tests;

public sealed class SshRemoteCommandExecutorLifecycleTests
{
    [Fact]
    public async Task DisposeAsyncCancelsAdmittedExecutionBeforeDisposingGate()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var profile = ServerProfile.Create(
            profileId,
            "Command lifecycle",
            "example.invalid",
            22,
            "operator",
            credentialReference: reference,
            authenticationKind: ServerAuthenticationKind.Password);
        var secrets = new BlockingSecretStore(reference);
        var factory = new SshRemoteCommandExecutorFactory(
            secrets,
            new UnexpectedHostTrustService(),
            new UnexpectedInteractivePrompt(),
            new SshSessionOptions(
                TimeSpan.FromSeconds(8),
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(250)));
        var executor = factory.Create(profile);

        var executionTask = executor.ExecuteAsync(
            RemoteCommandSpec.ReadOnly("uname", "-s"),
            CancellationToken.None);
        await secrets.ReadStarted.Task.WaitAsync(cancellationToken);

        var disposeTask = executor.DisposeAsync().AsTask();
        var result = await executionTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.OperationCancelled, result.Error?.Code);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            executor.ExecuteAsync(
                RemoteCommandSpec.ReadOnly("uname", "-s"),
                cancellationToken));
        await executor.DisposeAsync();
    }

    private sealed class BlockingSecretStore : ISecretStore
    {
        private readonly SecretReference _reference;

        public BlockingSecretStore(SecretReference reference)
        {
            _reference = reference;
        }

        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask SetAsync(
            SecretReference reference,
            string secret,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async ValueTask<string?> GetAsync(
            SecretReference reference,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(_reference, reference);
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        }

        public ValueTask DeleteAsync(
            SecretReference reference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class UnexpectedHostTrustService : IHostTrustService
    {
        public ValueTask<HostTrustVerification> VerifyAsync(
            HostKeyObservation observation,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The lifecycle test must cancel before host trust is reached.");
    }

    private sealed class UnexpectedInteractivePrompt : IInteractiveAuthenticationPrompt
    {
        public ValueTask<IReadOnlyList<string>?> PromptAsync(
            InteractiveAuthenticationChallenge challenge,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The lifecycle test must not request interactive authentication.");
    }
}
