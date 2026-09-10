using ServerDesk.Application.HostTrust;
using ServerDesk.Application.PortForwarding;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Networking;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Tests;

public sealed class SshPortForwardSessionLifecycleTests
{
    [Fact]
    public async Task DisposeAsyncCancelsAdmittedStartBeforeDisposingStateGate()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var profile = ServerProfile.Create(
            profileId,
            "Forward lifecycle",
            "example.invalid",
            22,
            "operator",
            credentialReference: reference,
            authenticationKind: ServerAuthenticationKind.Password);
        var forwardProfile = PortForwardProfile.Create(
            profile.Id,
            "Lifecycle local forward",
            PortForwardKind.Local,
            "127.0.0.1",
            0,
            "127.0.0.1",
            8080);
        var secrets = new BlockingSecretStore(reference);
        var factory = new SshPortForwardSessionFactory(
            secrets,
            new UnexpectedHostTrustService(),
            new UnexpectedInteractivePrompt(),
            new SshSessionOptions(
                TimeSpan.FromSeconds(8),
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(250)));
        var forward = factory.Create(profile, forwardProfile);

        var startTask = forward.StartAsync(CancellationToken.None).AsTask();
        await secrets.ReadStarted.Task.WaitAsync(cancellationToken);

        var disposeTask = forward.DisposeAsync().AsTask();

        await Assert.ThrowsAsync<OperationCanceledException>(() => startTask);
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        Assert.Equal(PortForwardSessionState.Stopped, forward.State);
        Assert.Equal(0, forward.BoundPort);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            forward.StopAsync(cancellationToken).AsTask());
        await forward.DisposeAsync();
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
