using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ServerDesk.Application.Agent;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Ssh.IntegrationTests;

public sealed class AgentLifecycleStagingIntegrationTests
{
    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int Port = int.Parse(
        Environment.GetEnvironmentVariable("SERVERDESK_SSH_PORT") ?? "2222",
        System.Globalization.CultureInfo.InvariantCulture);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string Password = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";

    [Fact]
    public async Task FixedAgentStageUploadsThroughSftpAndReverifiesLengthAndSha256()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Assert.Equal("127.0.0.1", Host);
        Assert.Equal("serverdesk_ci", Username);
        var planId = Guid.NewGuid();
        var stageDirectory = AgentLifecycleRemoteLayout.GetStagingDirectory(planId);
        var stagedBinary = AgentLifecycleRemoteLayout.GetStagedBinary(planId);
        var payload = Encoding.UTF8.GetBytes("serverdesk-agent-staging-integration-fixture");
        var expectedDigest = Convert.ToHexStringLower(SHA256.HashData(payload));

        await PrepareLocalFixtureAsync(stageDirectory, cancellationToken);
        try
        {
            var fixture = CreateFixture();
            await using (var fileSystem = fixture.FileSystemFactory.Create(fixture.Profile))
            {
                await fileSystem.ConnectAsync(cancellationToken);
                await using var source = new MemoryStream(payload, writable: false);
                await fileSystem.UploadAsync(
                    source,
                    stagedBinary,
                    payload.LongLength,
                    overwrite: false,
                    cancellationToken: cancellationToken);

                var stat = await fileSystem.StatAsync(stagedBinary, cancellationToken);
                Assert.Equal(RemoteFileKind.File, stat.Kind);
                Assert.Equal(payload.LongLength, stat.Size);
            }

            await using var executor = fixture.CommandFactory.Create(fixture.Profile);
            var length = await executor.ExecuteAsync(
                new RemoteCommandSpec(
                    "stat",
                    ["-c", "%s", "--", stagedBinary.Value],
                    TimeSpan.FromSeconds(10),
                    Environment: StableEnvironment()),
                cancellationToken);
            var hash = await executor.ExecuteAsync(
                new RemoteCommandSpec(
                    "sha256sum",
                    ["--", stagedBinary.Value],
                    TimeSpan.FromSeconds(10),
                    Environment: StableEnvironment()),
                cancellationToken);

            Assert.Null(length.Error);
            Assert.Null(hash.Error);
            Assert.Equal(0, length.Command?.ExitCode);
            Assert.Equal(0, hash.Command?.ExitCode);
            Assert.Equal(
                payload.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                length.Command?.StandardOutput.Trim());
            Assert.Equal(expectedDigest, hash.Command?.StandardOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]);
        }
        finally
        {
            await RemoveLocalFixtureAsync(stageDirectory, cancellationToken);
        }
    }

    private static IReadOnlyDictionary<string, string> StableEnvironment() =>
        new Dictionary<string, string>(StringComparer.Ordinal) { ["LC_ALL"] = "C" };

    private static async Task PrepareLocalFixtureAsync(RemotePath stageDirectory, CancellationToken cancellationToken)
    {
        await RunLocalSudoAsync(
            [
                "install", "-d", "-m", "0711", "--",
                AgentLifecyclePlanner.CacheDirectory,
                AgentLifecycleRemoteLayout.StagingRoot,
            ],
            cancellationToken);
        await RunLocalSudoAsync(
            [
                "install", "-d", "-m", "0700", "-o", Username, "--",
                stageDirectory.Value,
            ],
            cancellationToken);
    }

    private static Task RemoveLocalFixtureAsync(RemotePath stageDirectory, CancellationToken cancellationToken) =>
        RunLocalSudoAsync(["rm", "-rf", "--", stageDirectory.Value], cancellationToken);

    private static async Task RunLocalSudoAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("sudo")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-n");
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start local fixture sudo process.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await standardOutput;
        var error = await standardError;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Local staging fixture setup failed with exit code {process.ExitCode}. stdout={output.Trim()} stderr={error.Trim()}");
        }
    }

    private static Fixture CreateFixture()
    {
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var profile = ServerProfile.Create(
            profileId,
            "Agent staging fixture",
            Host,
            Port,
            Username,
            credentialReference: reference,
            authenticationKind: ServerAuthenticationKind.Password);
        var secretStore = new MemorySecretStore(reference, Password);
        var hostTrust = new TrustOnceHostTrustService();
        var interactivePrompt = new RejectInteractivePrompt();
        var options = new SshSessionOptions(
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(250));
        return new Fixture(
            profile,
            new SftpRemoteFileSystemFactory(secretStore, hostTrust, interactivePrompt, options),
            new SshRemoteCommandExecutorFactory(secretStore, hostTrust, interactivePrompt, options));
    }

    private sealed record Fixture(
        ServerProfile Profile,
        IRemoteFileSystemFactory FileSystemFactory,
        IRemoteCommandExecutorFactory CommandFactory);

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
            throw new InvalidOperationException("Password staging fixture must not request interactive authentication.");
    }
}
