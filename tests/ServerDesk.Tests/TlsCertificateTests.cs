using ServerDesk.Application.Nginx;
using ServerDesk.Application.Remote;
using ServerDesk.Application.Tls;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class TlsCertificateTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OpenSslParserReadsDatesSansAndFingerprint()
    {
        var parsed = OpenSslCertificateParser.Parse(OpenSslOutput("Sep 20 12:00:00 2026 GMT"));

        Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), parsed.NotBeforeUtc);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero), parsed.NotAfterUtc);
        Assert.Contains("example.test", parsed.SubjectAlternativeNames);
        Assert.Contains("www.example.test", parsed.SubjectAlternativeNames);
        Assert.Equal("AA:BB:CC", parsed.FingerprintSha256);
    }

    [Fact]
    public void CertbotParserUsesExplicitManagedCertificateFields()
    {
        const string output = """
            Found the following certificates:
              Certificate Name: example.test
                Domains: example.test www.example.test
                Expiry Date: 2026-09-20 12:00:00+00:00 (VALID: 23 days)
                Certificate Path: /etc/letsencrypt/live/example.test/fullchain.pem
                Private Key Path: /etc/letsencrypt/live/example.test/privkey.pem
            """;

        var managed = Assert.Single(CertbotOutputParser.ParseCertificates(output, 10));

        Assert.Equal("example.test", managed.Name);
        Assert.Equal(["example.test", "www.example.test"], managed.Domains);
        Assert.Equal("/etc/letsencrypt/live/example.test/fullchain.pem", managed.CertificatePath);
        Assert.Equal("/etc/letsencrypt/live/example.test/privkey.pem", managed.PrivateKeyPath);
    }

    [Theory]
    [InlineData("ubuntu-24.04", "app24.example.test", 1, 2026, 10, 30)]
    [InlineData("ubuntu-26.04", "edge26.example.test", 0, 2027, 7, 15)]
    [InlineData("debian-13", "legacy13.example.test", 1, 2026, 8, 20)]
    public void CertifiedDistroFixturesNormalizeOpenSslAndCertbotOutputs(
        string distro,
        string expectedDnsName,
        int expectedManagedCount,
        int expiryYear,
        int expiryMonth,
        int expiryDay)
    {
        var fixture = ReadCertifiedFixture(distro);

        var certificate = OpenSslCertificateParser.Parse(fixture.OpenSslOutput);
        var managed = CertbotOutputParser.ParseCertificates(fixture.CertbotOutput, 10);

        Assert.Contains(expectedDnsName, certificate.SubjectAlternativeNames);
        Assert.Equal(
            new DateTimeOffset(expiryYear, expiryMonth, expiryDay, certificate.NotAfterUtc.Hour, certificate.NotAfterUtc.Minute, certificate.NotAfterUtc.Second, TimeSpan.Zero),
            certificate.NotAfterUtc);
        Assert.Equal(expectedManagedCount, managed.Count);
        Assert.All(managed, item =>
        {
            Assert.StartsWith("/", item.CertificatePath, StringComparison.Ordinal);
            Assert.StartsWith("/", item.PrivateKeyPath, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task InventoryClassifiesExpiryAndNeverReadsPrivateKeyContent()
    {
        var state = State.Create(certbotManaged: false);
        var service = CreateService(state);

        var result = await service.InspectAsync(Profile(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var certificate = Assert.Single(result.Snapshot!.Certificates);
        Assert.Equal(TlsCertificateHealth.ExpiringSoon, certificate.Health);
        Assert.Equal(23, certificate.DaysRemaining);
        Assert.False(certificate.IsCertbotManaged);
        Assert.Contains("/etc/nginx/private/example.key", certificate.PrivateKeyPaths);
        Assert.DoesNotContain(state.Commands, command =>
            command.Executable == "openssl" && command.Arguments.Contains("/etc/nginx/private/example.key"));
        Assert.All(state.Commands.Where(command => command.Executable == "openssl"), command =>
            Assert.Equal("/etc/nginx/certs/example.pem", command.Arguments[2]));
    }

    [Fact]
    public async Task InventoryMapsOnlyExplicitCertbotCertificatePath()
    {
        var state = State.Create(certbotManaged: true);
        var service = CreateService(state);

        var result = await service.InspectAsync(Profile(), TestContext.Current.CancellationToken);

        var certificate = Assert.Single(result.Snapshot!.Certificates);
        Assert.Equal("example.test", certificate.CertbotCertificateName);
        Assert.Equal(CertbotRuntimeState.Available, result.Snapshot.Certbot.State);
        Assert.True(result.Snapshot.Certbot.NginxPluginAvailable);
    }

    [Fact]
    public async Task RenewRefusesUnmanagedCertificateBeforeMutation()
    {
        var state = State.Create(certbotManaged: false);
        var service = CreateService(state);

        var result = await service.RenewAsync(
            Profile(),
            "example.test",
            "/etc/nginx/certs/example.pem",
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.PathConflict, result.Error?.Code);
        Assert.DoesNotContain(state.Commands, command => command.Arguments.Contains("renew"));
    }

    [Fact]
    public async Task RenewTransportLossIsAmbiguousAndIsNotRetried()
    {
        var state = State.Create(certbotManaged: true);
        state.RenewError = new RemoteError(RemoteErrorCode.NetworkInterrupted, "connection dropped");
        var service = CreateService(state);

        var result = await service.RenewAsync(
            Profile(),
            "example.test",
            "/etc/nginx/certs/example.pem",
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.True(result.AmbiguousState);
        Assert.Equal(RemoteErrorCode.AmbiguousState, result.Error?.Code);
        Assert.Single(state.Commands, command => command.Arguments.Contains("renew"));
    }

    [Fact]
    public async Task ObtainRequiresDomainsToBelongToSelectedNginxSite()
    {
        var state = State.Create(certbotManaged: true);
        var service = CreateService(state);
        var request = new CertbotObtainRequest(
            "nginx:site",
            "other.test",
            ["other.test"],
            "admin@example.test",
            TermsAccepted: true);

        var result = await service.ObtainAsync(Profile(), request, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.PathConflict, result.Error?.Code);
        Assert.DoesNotContain(state.Commands, command => command.Arguments.Contains("certonly"));
    }

    private static TlsCertificateService CreateService(State state) =>
        new(
            new FakeNginxInventoryService(state.NginxSnapshot),
            new FakeCommandFactory(state),
            TlsCertificateOptions.Default,
            new FixedTimeProvider(Now));

    private static ServerProfile Profile() => ServerProfile.Create("tls", "example.invalid", 22, "dev");

    private static string OpenSslOutput(string notAfter) => $"""
        subject=CN = example.test
        issuer=CN = Example Test CA
        notBefore=Aug  1 00:00:00 2026 GMT
        notAfter={notAfter}
        X509v3 Subject Alternative Name:
            DNS:example.test, DNS:www.example.test
        sha256 Fingerprint=AA:BB:CC
        """;

    private static CertifiedFixture ReadCertifiedFixture(string distro)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Tls", distro + ".txt");
        var content = File.ReadAllText(path);
        const string openSslMarker = "---OPENSSL---\n";
        const string certbotMarker = "---CERTBOT---\n";
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.StartsWith(openSslMarker, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"TLS fixture '{distro}' is missing the OpenSSL section.");
        }

        var certbotIndex = normalized.IndexOf(certbotMarker, StringComparison.Ordinal);
        if (certbotIndex < 0)
        {
            throw new InvalidDataException($"TLS fixture '{distro}' is missing the Certbot section.");
        }

        return new CertifiedFixture(
            normalized[openSslMarker.Length..certbotIndex],
            normalized[(certbotIndex + certbotMarker.Length)..]);
    }

    private sealed record CertifiedFixture(string OpenSslOutput, string CertbotOutput);

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class State
    {
        public List<RemoteCommandSpec> Commands { get; } = [];
        public NginxInventorySnapshot NginxSnapshot { get; private init; } = null!;
        public bool CertbotManaged { get; private init; }
        public RemoteError? RenewError { get; set; }

        public static State Create(bool certbotManaged)
        {
            var site = new NginxSiteInfo(
                "nginx:site",
                "/etc/nginx/sites-enabled/example",
                0,
                ["example.test", "www.example.test"],
                ["443 ssl"],
                ["http://127.0.0.1:5000"],
                ["/etc/nginx/certs/example.pem"],
                ["/etc/nginx/private/example.key"],
                "server { listen 443 ssl; }");
            return new State
            {
                CertbotManaged = certbotManaged,
                NginxSnapshot = new NginxInventorySnapshot(
                    NginxRuntimeState.Available,
                    "1.26.3",
                    [site],
                    [],
                    string.Empty),
            };
        }
    }

    private sealed class FakeNginxInventoryService : INginxInventoryService
    {
        private readonly NginxInventorySnapshot _snapshot;
        public FakeNginxInventoryService(NginxInventorySnapshot snapshot) => _snapshot = snapshot;
        public Task<NginxInventoryResult> InspectAsync(ServerProfile profile, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new NginxInventoryResult(_snapshot, null));
        }
    }

    private sealed class FakeCommandFactory : IRemoteCommandExecutorFactory
    {
        private readonly State _state;
        public FakeCommandFactory(State state) => _state = state;
        public IRemoteCommandExecutor Create(ServerProfile profile) => new FakeExecutor(profile.Id, _state);
    }

    private sealed class FakeExecutor : IRemoteCommandExecutor
    {
        private readonly State _state;
        public FakeExecutor(Guid serverProfileId, State state)
        {
            ServerProfileId = serverProfileId;
            _state = state;
        }

        public Guid ServerProfileId { get; }

        public Task<RemoteExecutionResult> ExecuteAsync(RemoteCommandSpec command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _state.Commands.Add(command);
            if (command.Executable == "openssl")
            {
                return Success(OpenSslOutput("Sep 20 12:00:00 2026 GMT"));
            }

            if (command.Executable == "certbot" && command.Arguments.SequenceEqual(["--version"]))
            {
                return Success("certbot 5.7.0\n");
            }

            if (command.Executable == "certbot" && command.Arguments.Contains("plugins"))
            {
                return Success("* nginx\nDescription: Nginx Web Server plugin\n");
            }

            if (command.Executable == "sudo" && command.Arguments.Contains("certificates"))
            {
                return Success(_state.CertbotManaged
                    ? """
                        Found the following certificates:
                          Certificate Name: example.test
                            Domains: example.test www.example.test
                            Expiry Date: 2026-09-20 12:00:00+00:00 (VALID: 23 days)
                            Certificate Path: /etc/nginx/certs/example.pem
                            Private Key Path: /etc/nginx/private/example.key
                        """
                    : "No certificates found.\n");
            }

            if (command.Executable == "sudo" && command.Arguments.Contains("renew"))
            {
                return _state.RenewError is not null
                    ? Task.FromResult(RemoteExecutionResult.Failure(_state.RenewError))
                    : Success("Congratulations, all simulated renewals succeeded.\n");
            }

            return Success();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static Task<RemoteExecutionResult> Success(string output = "") =>
            Task.FromResult(RemoteExecutionResult.Success(
                new RemoteCommandResult(0, output, string.Empty, TimeSpan.Zero)));
    }
}
