using System.Globalization;
using System.Text;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Nginx;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Application.Tls;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Ssh.IntegrationTests;

public sealed class TlsCertificateIntegrationTests
{
    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int Port = int.Parse(Environment.GetEnvironmentVariable("SERVERDESK_SSH_PORT") ?? "2222", CultureInfo.InvariantCulture);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string Password = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";
    private static readonly string Home = Environment.GetEnvironmentVariable("SERVERDESK_SFTP_HOME") ?? $"/home/{Username}";

    [Fact]
    public async Task InventoryRenewAndObtainCrossRealOpenSshWithRealCertificateParsing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        var directory = RemotePath.Parse($"{Home}/serverdesk-tls-{Guid.NewGuid():N}");
        await InstallFixtureAsync(fixture, directory, cancellationToken);

        try
        {
            var certificatePath = directory.Combine("cert.pem").Value;
            var privateKeyPath = directory.Combine("key.pem").Value;
            var commands = new List<RemoteCommandSpec>();
            var rewritingFactory = new RewritingCommandFactory(
                fixture.CommandFactory,
                directory.Combine("tls-certbot-fixture.sh").Value,
                directory.Combine("tls-sudo-fixture.sh").Value,
                commands);
            var nginx = new StaticNginxInventoryService(CreateNginxSnapshot(certificatePath, privateKeyPath));
            var service = new TlsCertificateService(
                nginx,
                rewritingFactory,
                new TlsCertificateOptions(TimeSpan.FromSeconds(20), 30, 32, 256 * 1024),
                TimeProvider.System);

            var inventory = await service.InspectAsync(fixture.Profile, cancellationToken);

            Assert.True(inventory.IsSuccess, inventory.Error?.Message);
            var before = Assert.Single(inventory.Snapshot!.Certificates);
            Assert.NotEqual(TlsCertificateHealth.Unreadable, before.Health);
            Assert.Equal("example.test", before.CertbotCertificateName);
            Assert.Contains("example.test", before.SubjectAlternativeNames);
            Assert.Equal(CertbotRuntimeState.Available, inventory.Snapshot.Certbot.State);
            Assert.True(inventory.Snapshot.Certbot.NginxPluginAvailable);
            Assert.DoesNotContain(commands, command =>
                command.Executable == "openssl" && command.Arguments.Contains(privateKeyPath));

            var renew = await service.RenewAsync(
                fixture.Profile,
                "example.test",
                certificatePath,
                cancellationToken);

            Assert.True(renew.IsSuccess, renew.Error?.Message ?? renew.Message);
            Assert.True(renew.CertificateChanged);
            Assert.NotNull(renew.VerifiedCertificate);
            Assert.NotEqual(before.FingerprintSha256, renew.VerifiedCertificate!.FingerprintSha256);
            Assert.Contains("renew", await ReadTextAsync(fixture, directory.Combine("certbot.log"), cancellationToken));
            Assert.Contains("reload", await ReadTextAsync(fixture, directory.Combine("nginx.log"), cancellationToken));

            var obtain = await service.ObtainAsync(
                fixture.Profile,
                new CertbotObtainRequest(
                    "nginx:tls-fixture",
                    "new.example.test",
                    ["new.example.test"],
                    "admin@example.test",
                    TermsAccepted: true),
                cancellationToken);

            Assert.True(obtain.IsSuccess, obtain.Error?.Message ?? obtain.Message);
            Assert.NotNull(obtain.VerifiedCertificate);
            Assert.EndsWith("/new-cert.pem", obtain.VerifiedCertificate!.CertificatePath, StringComparison.Ordinal);
            Assert.Contains("new.example.test", obtain.VerifiedCertificate.SubjectAlternativeNames);
            Assert.Contains("obtain", await ReadTextAsync(fixture, directory.Combine("certbot.log"), cancellationToken));
            Assert.Single(commands, command => command.Executable == "sudo" && command.Arguments.Contains("renew"));
            Assert.Single(commands, command => command.Executable == "sudo" && command.Arguments.Contains("certonly"));
        }
        finally
        {
            await CleanupAsync(fixture, directory, CancellationToken.None);
        }
    }

    private static NginxInventorySnapshot CreateNginxSnapshot(string certificatePath, string privateKeyPath)
    {
        var site = new NginxSiteInfo(
            "nginx:tls-fixture",
            "/etc/nginx/sites-enabled/tls-fixture",
            0,
            ["example.test", "www.example.test", "new.example.test"],
            ["443 ssl"],
            ["http://127.0.0.1:5000"],
            [certificatePath],
            [privateKeyPath],
            "server { listen 443 ssl; }");
        return new NginxInventorySnapshot(
            NginxRuntimeState.Available,
            "1.26.3",
            [site],
            [],
            string.Empty);
    }

    private static async Task InstallFixtureAsync(Fixture fixture, RemotePath directory, CancellationToken cancellationToken)
    {
        await using var fileSystem = fixture.FileSystemFactory.Create(fixture.Profile);
        await fileSystem.ConnectAsync(cancellationToken);
        await fileSystem.CreateDirectoryAsync(directory, cancellationToken);

        foreach (var script in new[] { "tls-certbot-fixture.sh", "tls-sudo-fixture.sh" })
        {
            var content = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", script), cancellationToken);
            await UploadTextAsync(
                fileSystem,
                directory.Combine(script),
                content,
                RemoteUnixPermissions.FromMode(700),
                cancellationToken);
        }

        await using var executor = fixture.CommandFactory.Create(fixture.Profile);
        var generated = await executor.ExecuteAsync(
            new RemoteCommandSpec(
                "openssl",
                [
                    "req",
                    "-x509",
                    "-newkey",
                    "rsa:2048",
                    "-nodes",
                    "-keyout",
                    directory.Combine("key.pem").Value,
                    "-out",
                    directory.Combine("cert.pem").Value,
                    "-days",
                    "20",
                    "-subj",
                    "/CN=example.test",
                    "-addext",
                    "subjectAltName=DNS:example.test,DNS:www.example.test",
                ],
                TimeSpan.FromSeconds(20),
                OperationRisk.Mutating),
            cancellationToken);
        Assert.True(generated.IsSuccess, generated.Error?.Message ?? generated.Command?.StandardError);
        Assert.Equal(0, generated.Command!.ExitCode);
    }

    private static async Task UploadTextAsync(
        IRemoteFileSystem fileSystem,
        RemotePath path,
        string text,
        RemoteUnixPermissions permissions,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await using var stream = new MemoryStream(bytes, writable: false);
        await fileSystem.UploadAsync(stream, path, bytes.Length, overwrite: false, cancellationToken: cancellationToken);
        await fileSystem.SetPermissionsAsync(path, permissions, cancellationToken);
    }

    private static async Task<string> ReadTextAsync(Fixture fixture, RemotePath path, CancellationToken cancellationToken)
    {
        await using var fileSystem = fixture.FileSystemFactory.Create(fixture.Profile);
        await fileSystem.ConnectAsync(cancellationToken);
        await using var buffer = new MemoryStream();
        await fileSystem.DownloadAsync(path, buffer, cancellationToken: cancellationToken);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task CleanupAsync(Fixture fixture, RemotePath directory, CancellationToken cancellationToken)
    {
        await using var executor = fixture.CommandFactory.Create(fixture.Profile);
        try
        {
            _ = await executor.ExecuteAsync(
                new RemoteCommandSpec(
                    "rm",
                    ["-rf", "--", directory.Value],
                    TimeSpan.FromSeconds(10),
                    OperationRisk.Destructive),
                cancellationToken);
        }
        catch
        {
        }
    }

    private static Fixture CreateFixture()
    {
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var profile = ServerProfile.Create(
            profileId,
            "TLS fixture",
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
        return new Fixture(
            profile,
            new SshRemoteCommandExecutorFactory(secretStore, trust, prompt, options),
            new SftpRemoteFileSystemFactory(secretStore, trust, prompt, options));
    }

    private sealed record Fixture(
        ServerProfile Profile,
        IRemoteCommandExecutorFactory CommandFactory,
        IRemoteFileSystemFactory FileSystemFactory);

    private sealed class StaticNginxInventoryService : INginxInventoryService
    {
        private readonly NginxInventorySnapshot _snapshot;
        public StaticNginxInventoryService(NginxInventorySnapshot snapshot) => _snapshot = snapshot;
        public Task<NginxInventoryResult> InspectAsync(ServerProfile profile, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new NginxInventoryResult(_snapshot, null));
        }
    }

    private sealed class RewritingCommandFactory : IRemoteCommandExecutorFactory
    {
        private readonly IRemoteCommandExecutorFactory _inner;
        private readonly string _certbotPath;
        private readonly string _sudoPath;
        private readonly List<RemoteCommandSpec> _commands;

        public RewritingCommandFactory(
            IRemoteCommandExecutorFactory inner,
            string certbotPath,
            string sudoPath,
            List<RemoteCommandSpec> commands)
        {
            _inner = inner;
            _certbotPath = certbotPath;
            _sudoPath = sudoPath;
            _commands = commands;
        }

        public IRemoteCommandExecutor Create(ServerProfile profile) =>
            new RewritingExecutor(_inner.Create(profile), _certbotPath, _sudoPath, _commands);
    }

    private sealed class RewritingExecutor : IRemoteCommandExecutor
    {
        private readonly IRemoteCommandExecutor _inner;
        private readonly string _certbotPath;
        private readonly string _sudoPath;
        private readonly List<RemoteCommandSpec> _commands;

        public RewritingExecutor(
            IRemoteCommandExecutor inner,
            string certbotPath,
            string sudoPath,
            List<RemoteCommandSpec> commands)
        {
            _inner = inner;
            _certbotPath = certbotPath;
            _sudoPath = sudoPath;
            _commands = commands;
        }

        public Guid ServerProfileId => _inner.ServerProfileId;

        public Task<RemoteExecutionResult> ExecuteAsync(RemoteCommandSpec command, CancellationToken cancellationToken = default)
        {
            _commands.Add(command);
            var executable = command.Executable switch
            {
                "certbot" => _certbotPath,
                "sudo" => _sudoPath,
                _ => command.Executable,
            };
            return _inner.ExecuteAsync(command with { Executable = executable }, cancellationToken);
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
            return ValueTask.FromResult(new HostTrustVerification(HostTrustOutcome.TrustedOnce, observation, []));
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
