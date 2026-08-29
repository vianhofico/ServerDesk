using System.Globalization;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Packages;
using ServerDesk.Application.Remote;
using ServerDesk.Application.Secrets;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Ssh.IntegrationTests;

public sealed class PackageAdministrationIntegrationTests
{
    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int Port = int.Parse(
        Environment.GetEnvironmentVariable("SERVERDESK_SSH_PORT") ?? "2222",
        CultureInfo.InvariantCulture);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string Password = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";

    [Fact]
    public async Task SelectedAptUpgradeCrossesOpenSshAsTypedEchoWithoutChangingRunnerPackages()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        var state = new PackageFixtureState();
        var factory = new PackageFixtureCommandFactory(fixture.CommandFactory, state);
        var service = new PackageAdministrationService(
            factory,
            PackageAdministrationOptions.Default with { PrivilegeExecutable = "/bin/echo" });

        var previewResult = await service.PreviewAsync(
            fixture.Profile,
            new PackageMutationRequest(
                PackageMutationKind.Upgrade,
                PackageManagerKind.Apt,
                ["nginx"]),
            cancellationToken);

        Assert.True(previewResult.IsSuccess, previewResult.Error?.Message);
        var preview = Assert.IsType<PackageMutationPreview>(previewResult.Preview);
        Assert.Equal("/bin/echo", preview.Executable);
        Assert.Equal(
            ["-n", "apt-get", "-y", "--only-upgrade", "install", "nginx"],
            preview.Arguments);
        Assert.Equal("2.0-fixture", Assert.Single(preview.BoundPackages).CandidateVersion);

        var result = await service.ExecuteAsync(fixture.Profile, preview, cancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("2.0-fixture", state.InstalledVersion);
        var mutation = Assert.Single(factory.Mutations);
        Assert.Equal("/bin/echo", mutation.Executable);
        Assert.Equal(preview.Arguments, mutation.Arguments);
    }

    private static Fixture CreateFixture()
    {
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var profile = ServerProfile.Create(
            profileId,
            "Package admin fixture",
            Host,
            Port,
            Username,
            credentialReference: reference,
            authenticationKind: ServerAuthenticationKind.Password);
        var secretStore = new MemorySecretStore(reference, Password);
        var options = new SshSessionOptions(
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(250));
        return new Fixture(
            profile,
            new SshRemoteCommandExecutorFactory(
                secretStore,
                new TrustOnceHostTrustService(),
                new RejectInteractivePrompt(),
                options));
    }

    private static RemoteExecutionResult Success(string output, int exitCode = 0, string error = "") =>
        RemoteExecutionResult.Success(new RemoteCommandResult(
            exitCode,
            output,
            error,
            TimeSpan.FromMilliseconds(1)));

    private sealed record Fixture(
        ServerProfile Profile,
        IRemoteCommandExecutorFactory CommandFactory);

    private sealed class PackageFixtureState
    {
        public string InstalledVersion { get; set; } = "1.0-fixture";
    }

    private sealed class PackageFixtureCommandFactory : IRemoteCommandExecutorFactory
    {
        private readonly IRemoteCommandExecutorFactory _inner;
        private readonly PackageFixtureState _state;

        public PackageFixtureCommandFactory(
            IRemoteCommandExecutorFactory inner,
            PackageFixtureState state)
        {
            _inner = inner;
            _state = state;
        }

        public List<RemoteCommandSpec> Mutations { get; } = [];

        public IRemoteCommandExecutor Create(ServerProfile profile) =>
            new PackageFixtureExecutor(_inner.Create(profile), _state, Mutations);
    }

    private sealed class PackageFixtureExecutor : IRemoteCommandExecutor
    {
        private readonly IRemoteCommandExecutor _inner;
        private readonly PackageFixtureState _state;
        private readonly List<RemoteCommandSpec> _mutations;

        public PackageFixtureExecutor(
            IRemoteCommandExecutor inner,
            PackageFixtureState state,
            List<RemoteCommandSpec> mutations)
        {
            _inner = inner;
            _state = state;
            _mutations = mutations;
        }

        public Guid ServerProfileId => _inner.ServerProfileId;

        public async Task<RemoteExecutionResult> ExecuteAsync(
            RemoteCommandSpec command,
            CancellationToken cancellationToken = default)
        {
            if (command.Executable == "apt-get" && command.Arguments.SequenceEqual(["--version"]))
            {
                return Success("apt 2.8.3 fixture\n");
            }

            if (command.Executable == "dpkg-query" && command.Arguments.SequenceEqual(["--version"]))
            {
                return Success("Debian dpkg-query fixture\n");
            }

            if (command.Executable == "dnf" && command.Arguments.SequenceEqual(["--version"]))
            {
                return Success(string.Empty, 127, "dnf: command not found");
            }

            if (command.Executable == "rpm" && command.Arguments.SequenceEqual(["--version"]))
            {
                return Success(string.Empty, 127, "rpm: command not found");
            }

            if (command.Executable == "dpkg-query" && command.Arguments.Count > 0 && command.Arguments[0] == "-W")
            {
                return Success($"nginx\t{_state.InstalledVersion}\tamd64\n");
            }

            if (command.Executable == "apt-get" && command.Arguments.Contains("upgrade", StringComparer.Ordinal))
            {
                return _state.InstalledVersion == "1.0-fixture"
                    ? Success("Inst nginx [1.0-fixture] (2.0-fixture Ubuntu:24.04/noble-updates [amd64])\n")
                    : Success(string.Empty);
            }

            if (command.Executable == "apt-cache" && command.Arguments.Count > 0 && command.Arguments[0] == "policy")
            {
                return Success(
                    $"nginx:\n  Installed: {_state.InstalledVersion}\n  Candidate: 2.0-fixture\n");
            }

            if (command.Executable == "/bin/echo")
            {
                _mutations.Add(command);
                var execution = await _inner.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
                if (execution.Error is null && execution.Command?.ExitCode == 0 &&
                    command.Arguments.Contains("--only-upgrade", StringComparer.Ordinal))
                {
                    _state.InstalledVersion = "2.0-fixture";
                }

                return execution;
            }

            throw new InvalidOperationException($"Unexpected package fixture command: {command.Executable} {string.Join(' ', command.Arguments)}");
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
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
