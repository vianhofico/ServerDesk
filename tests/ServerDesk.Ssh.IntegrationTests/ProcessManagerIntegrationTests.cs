using System.Globalization;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Processes;
using ServerDesk.Application.Remote;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Ssh.IntegrationTests;

public sealed class ProcessManagerIntegrationTests
{
    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int Port = int.Parse(
        Environment.GetEnvironmentVariable("SERVERDESK_SSH_PORT") ?? "2222",
        CultureInfo.InvariantCulture);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string Password = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";

    [Fact]
    public async Task ListFindsControlledProcessAndTerminateRemovesIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        int? processId = null;

        try
        {
            await using (var executor = fixture.CommandFactory.Create(fixture.Profile))
            {
                var spawn = await executor.ExecuteAsync(
                    new RemoteCommandSpec(
                        "/bin/sh",
                        ["-c", "sleep 30 >/dev/null 2>&1 & echo $!"],
                        TimeSpan.FromSeconds(5)),
                    cancellationToken);
                Assert.True(spawn.IsSuccess, spawn.Error?.Message);
                Assert.Equal(0, spawn.Command!.ExitCode);
                Assert.True(int.TryParse(spawn.Command.StandardOutput.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var pid));
                processId = pid;
            }

            var list = await fixture.Service.ListAsync(fixture.Profile, cancellationToken);
            Assert.True(list.IsSuccess, list.Error?.Message);
            Assert.Contains(list.Processes, process => process.ProcessId == processId);

            var terminate = await fixture.Service.SignalAsync(
                fixture.Profile,
                processId!.Value,
                ServerProcessSignal.Terminate,
                cancellationToken);
            Assert.True(terminate.IsSuccess, terminate.Message);

            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(100, cancellationToken);
                var current = await fixture.Service.GetAsync(fixture.Profile, processId.Value, cancellationToken);
                Assert.True(current.IsSuccess, current.Error?.Message);
                if (current.Processes.Count == 0)
                {
                    processId = null;
                    return;
                }
            }

            Assert.Fail("Controlled process still appeared in ps after SIGTERM.");
        }
        finally
        {
            if (processId is { } remaining)
            {
                try
                {
                    _ = await fixture.Service.SignalAsync(
                        fixture.Profile,
                        remaining,
                        ServerProcessSignal.ForceKill,
                        CancellationToken.None);
                }
                catch
                {
                }
            }
        }
    }

    private static ProcessFixture CreateFixture()
    {
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var profile = ServerProfile.Create(
            profileId,
            "Process fixture",
            Host,
            Port,
            Username,
            credentialReference: reference,
            authenticationKind: ServerAuthenticationKind.Password);
        var secretStore = new MemorySecretStore(reference, Password);
        var trust = new TrustOnceHostTrustService();
        var prompt = new RejectInteractivePrompt();
        var options = new SshSessionOptions(
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(250));
        var commandFactory = new SshRemoteCommandExecutorFactory(secretStore, trust, prompt, options);
        return new ProcessFixture(profile, commandFactory, new ServerProcessService(commandFactory));
    }

    private sealed record ProcessFixture(
        ServerProfile Profile,
        IRemoteCommandExecutorFactory CommandFactory,
        IServerProcessService Service);

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly SecretReference _reference;
        private readonly string _secret;

        public MemorySecretStore(SecretReference reference, string secret)
        {
            _reference = reference;
            _secret = secret;
        }

        public ValueTask SetAsync(SecretReference reference, string secret, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<string?> GetAsync(SecretReference reference, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<string?>(reference == _reference ? _secret : null);
        }

        public ValueTask DeleteAsync(SecretReference reference, CancellationToken cancellationToken = default) =>
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
            throw new InvalidOperationException("Password fixture must not request keyboard-interactive authentication.");
    }
}
