using System.Net;
using System.Net.Sockets;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Ssh.IntegrationTests;

public sealed class SshCancellationIntegrationTests
{
    [Fact]
    public async Task CallerCancellationTransitionsSessionToDisconnected()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        await using var endpoint = new HangingTcpEndpoint();
        var (profile, secretStore) = CreatePasswordProfile(endpoint.Port);
        var options = new SshSessionOptions(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(250));
        var factory = new SshRemoteSessionFactory(
            secretStore,
            new TrustOnceHostTrustService(),
            new RejectInteractivePrompt(),
            options);

        await using var session = factory.Create(profile);
        using var callerCancellation = CancellationTokenSource.CreateLinkedTokenSource(testCancellation);
        callerCancellation.CancelAfter(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await session.ConnectAsync(callerCancellation.Token));

        Assert.Equal(RemoteSessionState.Disconnected, session.State);
        Assert.Equal(RemoteErrorCode.OperationCancelled, session.LastError?.Code);
    }

    [Fact]
    public async Task ConnectTimeoutTransitionsSessionToFaulted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var endpoint = new HangingTcpEndpoint();
        var (profile, secretStore) = CreatePasswordProfile(endpoint.Port);
        var options = new SshSessionOptions(
            TimeSpan.FromMilliseconds(450),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(250));
        var factory = new SshRemoteSessionFactory(
            secretStore,
            new TrustOnceHostTrustService(),
            new RejectInteractivePrompt(),
            options);

        await using var session = factory.Create(profile);
        var exception = await Assert.ThrowsAsync<RemoteSessionException>(async () =>
            await session.ConnectAsync(cancellationToken));

        Assert.Equal(RemoteErrorCode.ConnectionFailed, exception.Error.Code);
        Assert.Equal(RemoteSessionState.Faulted, session.State);
        Assert.Equal(RemoteErrorCode.ConnectionFailed, session.LastError?.Code);
    }

    private static (ServerProfile Profile, MemorySecretStore SecretStore) CreatePasswordProfile(int port)
    {
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var profile = ServerProfile.Create(
            profileId,
            "Hanging SSH fixture",
            IPAddress.Loopback.ToString(),
            port,
            "serverdesk_ci",
            credentialReference: reference,
            authenticationKind: ServerAuthenticationKind.Password);
        return (profile, new MemorySecretStore(reference, "fixture-secret"));
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly SecretReference _reference;
        private readonly string _secret;

        public MemorySecretStore(SecretReference reference, string secret)
        {
            _reference = reference;
            _secret = secret;
        }

        public ValueTask SetAsync(
            SecretReference reference,
            string secret,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<string?> GetAsync(
            SecretReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<string?>(reference == _reference ? _secret : null);
        }

        public ValueTask DeleteAsync(
            SecretReference reference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TrustOnceHostTrustService : IHostTrustService
    {
        public ValueTask<HostTrustVerification> VerifyAsync(
            HostKeyObservation observation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new HostTrustVerification(
                HostTrustOutcome.TrustedOnce,
                observation,
                []));
        }
    }

    private sealed class RejectInteractivePrompt : IInteractiveAuthenticationPrompt
    {
        public ValueTask<IReadOnlyList<string>?> PromptAsync(
            InteractiveAuthenticationChallenge challenge,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The hanging fixture must never reach authentication.");
    }

    private sealed class HangingTcpEndpoint : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task _acceptTask;
        private TcpClient? _client;

        public HangingTcpEndpoint()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptTask = AcceptAndHoldAsync();
        }

        public int Port { get; }

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            _listener.Stop();
            _client?.Dispose();

            try
            {
                await _acceptTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            _lifetime.Dispose();
        }

        private async Task AcceptAndHoldAsync()
        {
            try
            {
                _client = await _listener.AcceptTcpClientAsync(_lifetime.Token).ConfigureAwait(false);
                await Task.Delay(Timeout.InfiniteTimeSpan, _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (SocketException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested)
            {
            }
        }
    }
}
