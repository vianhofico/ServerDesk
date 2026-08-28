using System.Net.Mail;
using System.Text;
using ServerDesk.Application.Audit;
using ServerDesk.Application.Nginx;
using ServerDesk.Application.Remote;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Tls;

public interface ITlsCertificateService
{
    Task<TlsCertificateInventoryResult> InspectAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default);

    Task<CertbotMutationResult> RenewAsync(
        ServerProfile profile,
        string certificateName,
        string expectedCertificatePath,
        CancellationToken cancellationToken = default);

    Task<CertbotMutationResult> ObtainAsync(
        ServerProfile profile,
        CertbotObtainRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class TlsCertificateService : ITlsCertificateService
{
    private static readonly IReadOnlyDictionary<string, string> StableEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["LC_ALL"] = "C" };

    private readonly INginxInventoryService _nginx;
    private readonly IRemoteCommandExecutorFactory _commandFactory;
    private readonly TlsCertificateOptions _options;
    private readonly TimeProvider _timeProvider;

    public TlsCertificateService(
        INginxInventoryService nginx,
        IRemoteCommandExecutorFactory commandFactory,
        TlsCertificateOptions options,
        TimeProvider timeProvider)
    {
        _nginx = nginx ?? throw new ArgumentNullException(nameof(nginx));
        _commandFactory = commandFactory ?? throw new ArgumentNullException(nameof(commandFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options.Validate();
    }

    public async Task<TlsCertificateInventoryResult> InspectAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var nginx = await _nginx.InspectAsync(profile, cancellationToken).ConfigureAwait(false);
        if (!nginx.IsSuccess || nginx.Snapshot is null)
        {
            return new TlsCertificateInventoryResult(null, nginx.Error ?? new RemoteError(
                RemoteErrorCode.CapabilityUnavailable,
                "ServerDesk could not inspect nginx before building TLS certificate inventory."));
        }

        var references = BuildReferences(nginx.Snapshot);
        if (references.Count > _options.MaximumCertificates)
        {
            return new TlsCertificateInventoryResult(
                null,
                new RemoteError(
                    RemoteErrorCode.CapabilityUnavailable,
                    $"nginx references more than {_options.MaximumCertificates} unique certificate files."));
        }

        await using var executor = _commandFactory.Create(profile);
        var certbot = await ProbeCertbotAsync(executor, cancellationToken).ConfigureAwait(false);
        var managedByPath = certbot.ManagedCertificates
            .GroupBy(item => item.CertificatePath, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var now = _timeProvider.GetUtcNow();
        var certificates = new List<TlsCertificateInfo>(references.Count);
        foreach (var reference in references.Values.OrderBy(item => item.CertificatePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            managedByPath.TryGetValue(reference.CertificatePath, out var managed);
            var certificate = await ReadCertificateAsync(
                    executor,
                    reference.CertificatePath,
                    reference.PrivateKeyPaths,
                    reference.SiteNames,
                    managed?.Name,
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
            certificates.Add(certificate);
        }

        return new TlsCertificateInventoryResult(
            new TlsCertificateInventorySnapshot(certificates, certbot, now),
            null);
    }

    public async Task<CertbotMutationResult> RenewAsync(
        ServerProfile profile,
        string certificateName,
        string expectedCertificatePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var name = NormalizeCertificateName(certificateName);
        var expectedPath = NormalizeAbsolutePath(expectedCertificatePath, nameof(expectedCertificatePath));
        var mutationStarted = false;
        try
        {
            var beforeInventory = await InspectAsync(profile, cancellationToken).ConfigureAwait(false);
            if (!beforeInventory.IsSuccess || beforeInventory.Snapshot is null)
            {
                return Failure(beforeInventory.Error ?? new RemoteError(RemoteErrorCode.CapabilityUnavailable, "TLS inventory is unavailable."));
            }

            var capability = beforeInventory.Snapshot.Certbot;
            if (!capability.CanMutate)
            {
                return Failure(CertbotUnavailable(capability));
            }

            var managed = capability.ManagedCertificates.FirstOrDefault(item =>
                string.Equals(item.Name, name, StringComparison.Ordinal));
            if (managed is null || !string.Equals(managed.CertificatePath, expectedPath, StringComparison.Ordinal))
            {
                return Failure(new RemoteError(
                    RemoteErrorCode.PathConflict,
                    "The selected certificate is not currently identified as the expected Certbot-managed lineage. Refresh before renewing."));
            }

            var before = beforeInventory.Snapshot.Certificates.FirstOrDefault(item =>
                string.Equals(item.CertificatePath, expectedPath, StringComparison.Ordinal));
            if (before is null || before.Health == TlsCertificateHealth.Unreadable)
            {
                return Failure(new RemoteError(
                    RemoteErrorCode.CapabilityUnavailable,
                    "The managed certificate must be readable and verified before renewal."));
            }

            await using var executor = _commandFactory.Create(profile);
            mutationStarted = true;
            var renewal = await ExecuteAsync(
                    executor,
                    "sudo",
                    [
                        "-n",
                        "certbot",
                        "renew",
                        "--cert-name",
                        name,
                        "--non-interactive",
                        "--no-random-sleep-on-renew",
                    ],
                    OperationRisk.Destructive,
                    ambiguousOnTransport: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!renewal.IsSuccess)
            {
                return renewal.ToMutationResult();
            }

            var afterInventory = await InspectAfterMutationAsync(profile, cancellationToken).ConfigureAwait(false);
            if (!afterInventory.IsSuccess || afterInventory.Snapshot is null)
            {
                return Ambiguous("Certbot returned success, but ServerDesk could not rebuild certificate inventory for verification.");
            }

            var afterManaged = afterInventory.Snapshot.Certbot.ManagedCertificates.FirstOrDefault(item =>
                string.Equals(item.Name, name, StringComparison.Ordinal) &&
                string.Equals(item.CertificatePath, expectedPath, StringComparison.Ordinal));
            var after = afterInventory.Snapshot.Certificates.FirstOrDefault(item =>
                string.Equals(item.CertificatePath, expectedPath, StringComparison.Ordinal));
            if (afterManaged is null || after is null || after.Health == TlsCertificateHealth.Unreadable)
            {
                return Ambiguous("Certbot returned success, but the managed certificate identity or certificate file could not be verified.");
            }

            var changed = HasCertificateChanged(before, after);
            if (after.NotAfterUtc < before.NotAfterUtc)
            {
                return new CertbotMutationResult(
                    false,
                    false,
                    changed,
                    "Certbot completed, but the verified certificate expiry moved backwards.",
                    new RemoteError(RemoteErrorCode.CommandFailed, "The renewed certificate has an earlier expiration date than the previous certificate."),
                    after);
            }

            var nginxTest = await VerifyNginxAsync(executor, mutationHasStarted: true, cancellationToken).ConfigureAwait(false);
            if (!nginxTest.IsSuccess)
            {
                return nginxTest.ToMutationResult(changed, after);
            }

            if (changed)
            {
                var reload = await ExecuteAsync(
                        executor,
                        "sudo",
                        ["-n", "nginx", "-s", "reload"],
                        OperationRisk.Destructive,
                        ambiguousOnTransport: true,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!reload.IsSuccess)
                {
                    return reload.ToMutationResult(changed, after);
                }
            }

            return new CertbotMutationResult(
                true,
                false,
                changed,
                changed
                    ? "Certbot renewed the certificate; nginx validation and reload were verified."
                    : "Certbot completed successfully; the certificate was not due for replacement and nginx validation passed.",
                VerifiedCertificate: after);
        }
        catch (OperationCanceledException) when (mutationStarted)
        {
            return Ambiguous("The Certbot renewal was cancelled after mutation started. Refresh certificate and nginx state before retrying.");
        }
    }

    public async Task<CertbotMutationResult> ObtainAsync(
        ServerProfile profile,
        CertbotObtainRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(request);
        var validationError = ValidateObtainRequest(request);
        if (validationError is not null)
        {
            return Failure(validationError);
        }

        var name = NormalizeCertificateName(request.CertificateName);
        var domains = request.Domains
            .Select(domain => domain.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var mutationStarted = false;
        try
        {
            var nginx = await _nginx.InspectAsync(profile, cancellationToken).ConfigureAwait(false);
            if (!nginx.IsSuccess || nginx.Snapshot is null)
            {
                return Failure(nginx.Error ?? new RemoteError(RemoteErrorCode.CapabilityUnavailable, "nginx inventory is unavailable."));
            }

            var site = nginx.Snapshot.Sites.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, request.NginxSiteId, StringComparison.Ordinal));
            if (site is null)
            {
                return Failure(new RemoteError(RemoteErrorCode.PathConflict, "The selected nginx site no longer exists. Refresh before obtaining a certificate."));
            }

            var normalizedSiteNames = site.ServerNames
                .Select(value => value.Trim().ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (domains.Any(domain => !normalizedSiteNames.Contains(domain)))
            {
                return Failure(new RemoteError(
                    RemoteErrorCode.PathConflict,
                    "Every requested domain must be an explicit server_name on the selected nginx site."));
            }

            await using var executor = _commandFactory.Create(profile);
            var capability = await ProbeCertbotAsync(executor, cancellationToken).ConfigureAwait(false);
            if (!capability.CanMutate || !capability.NginxPluginAvailable)
            {
                return Failure(CertbotUnavailable(capability, requireNginxPlugin: true));
            }

            if (capability.ManagedCertificates.Any(item => string.Equals(item.Name, name, StringComparison.Ordinal)))
            {
                return Failure(new RemoteError(
                    RemoteErrorCode.PathConflict,
                    "A Certbot lineage with this certificate name already exists. Use Renew for existing managed certificates."));
            }

            var arguments = new List<string>
            {
                "-n",
                "certbot",
                "certonly",
                "--nginx",
                "--non-interactive",
                "--agree-tos",
                "--email",
                request.Email.Trim(),
                "--cert-name",
                name,
            };
            foreach (var domain in domains)
            {
                arguments.Add("-d");
                arguments.Add(domain);
            }

            mutationStarted = true;
            var obtain = await ExecuteAsync(
                    executor,
                    "sudo",
                    arguments,
                    OperationRisk.Mutating,
                    ambiguousOnTransport: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!obtain.IsSuccess)
            {
                return obtain.ToMutationResult();
            }

            var afterCapability = await ProbeCertbotAfterMutationAsync(executor, cancellationToken).ConfigureAwait(false);
            if (afterCapability.State != CertbotRuntimeState.Available)
            {
                return Ambiguous("Certbot returned success, but the managed certificate list could not be verified afterwards.");
            }

            var managed = afterCapability.ManagedCertificates.FirstOrDefault(item =>
                string.Equals(item.Name, name, StringComparison.Ordinal));
            if (managed is null)
            {
                return Ambiguous("Certbot returned success, but the requested certificate lineage is absent from the verified managed-certificate list.");
            }

            var now = _timeProvider.GetUtcNow();
            var certificate = await ReadCertificateAsync(
                    executor,
                    managed.CertificatePath,
                    [managed.PrivateKeyPath],
                    [site.DisplayName],
                    managed.Name,
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
            if (certificate.Health == TlsCertificateHealth.Unreadable)
            {
                return Ambiguous("Certbot returned success, but the newly managed certificate could not be read with OpenSSL for verification.");
            }

            var nginxTest = await VerifyNginxAsync(executor, mutationHasStarted: true, cancellationToken).ConfigureAwait(false);
            if (!nginxTest.IsSuccess)
            {
                return nginxTest.ToMutationResult(certificateChanged: true, certificate);
            }

            return new CertbotMutationResult(
                true,
                false,
                true,
                "Certbot obtained and verified the certificate without rewriting nginx. Use the guarded nginx editor to attach the certificate paths to the site.",
                VerifiedCertificate: certificate);
        }
        catch (OperationCanceledException) when (mutationStarted)
        {
            return Ambiguous("The Certbot obtain request was cancelled after mutation started. Refresh Certbot and nginx state before retrying.");
        }
    }

    private Dictionary<string, CertificateReference> BuildReferences(NginxInventorySnapshot snapshot)
    {
        var references = new Dictionary<string, CertificateReference>(StringComparer.Ordinal);
        foreach (var site in snapshot.Sites)
        {
            foreach (var certificatePath in site.CertificatePaths)
            {
                if (string.IsNullOrWhiteSpace(certificatePath))
                {
                    continue;
                }

                if (!references.TryGetValue(certificatePath, out var reference))
                {
                    reference = new CertificateReference(certificatePath);
                    references.Add(certificatePath, reference);
                }

                reference.SiteNames.Add(site.DisplayName);
                foreach (var keyPath in site.CertificateKeyPaths.Where(value => !string.IsNullOrWhiteSpace(value)))
                {
                    reference.PrivateKeyPaths.Add(keyPath);
                }
            }
        }

        return references;
    }

    private async Task<TlsCertificateInfo> ReadCertificateAsync(
        IRemoteCommandExecutor executor,
        string certificatePath,
        IReadOnlyCollection<string> privateKeyPaths,
        IReadOnlyCollection<string> siteNames,
        string? certbotName,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeAbsolutePath(certificatePath, out var normalizedPath))
        {
            return Unreadable(certificatePath, privateKeyPaths, siteNames, certbotName, "Certificate path is not a safe absolute remote path.");
        }

        var execution = await executor.ExecuteAsync(
                new RemoteCommandSpec(
                    "openssl",
                    [
                        "x509",
                        "-in",
                        normalizedPath,
                        "-noout",
                        "-subject",
                        "-issuer",
                        "-startdate",
                        "-enddate",
                        "-ext",
                        "subjectAltName",
                        "-sha256",
                        "-fingerprint",
                    ],
                    _options.CommandTimeout,
                    OperationRisk.ReadOnly,
                    StableEnvironment),
                cancellationToken)
            .ConfigureAwait(false);
        if (execution.Error is not null)
        {
            return Unreadable(normalizedPath, privateKeyPaths, siteNames, certbotName, execution.Error.Message);
        }

        var command = execution.Command!;
        if (command.ExitCode != 0)
        {
            return Unreadable(
                normalizedPath,
                privateKeyPaths,
                siteNames,
                certbotName,
                FirstUseful(command.StandardError, command.StandardOutput, "OpenSSL could not read the certificate."));
        }

        try
        {
            var parsed = OpenSslCertificateParser.Parse(command.StandardOutput);
            var health = ClassifyHealth(parsed.NotBeforeUtc, parsed.NotAfterUtc, now);
            var days = (int)Math.Floor((parsed.NotAfterUtc - now).TotalDays);
            return new TlsCertificateInfo(
                normalizedPath,
                privateKeyPaths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                parsed.Subject,
                parsed.SubjectAlternativeNames,
                parsed.Issuer,
                parsed.NotBeforeUtc,
                parsed.NotAfterUtc,
                days,
                parsed.FingerprintSha256,
                health,
                siteNames.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                certbotName);
        }
        catch (FormatException exception)
        {
            return Unreadable(normalizedPath, privateKeyPaths, siteNames, certbotName, exception.Message);
        }
    }

    private async Task<CertbotCapability> ProbeCertbotAsync(
        IRemoteCommandExecutor executor,
        CancellationToken cancellationToken)
    {
        var versionExecution = await executor.ExecuteAsync(
                ReadOnly("certbot", ["--version"]),
                cancellationToken)
            .ConfigureAwait(false);
        if (versionExecution.Error is not null)
        {
            return versionExecution.Error.Code == RemoteErrorCode.CommandNotFound
                ? new CertbotCapability(CertbotRuntimeState.CliMissing, null, false, [], versionExecution.Error.Message)
                : new CertbotCapability(CertbotRuntimeState.ProbeFailed, null, false, [], versionExecution.Error.Message);
        }

        var versionCommand = versionExecution.Command!;
        if (versionCommand.ExitCode != 0)
        {
            return new CertbotCapability(
                CertbotRuntimeState.ProbeFailed,
                CertbotOutputParser.ParseVersion(versionCommand.StandardOutput, versionCommand.StandardError),
                false,
                [],
                FirstUseful(versionCommand.StandardError, versionCommand.StandardOutput, "Certbot version probe failed."));
        }

        var version = CertbotOutputParser.ParseVersion(versionCommand.StandardOutput, versionCommand.StandardError);
        var pluginsExecution = await executor.ExecuteAsync(
                ReadOnly("certbot", ["plugins", "--text"]),
                cancellationToken)
            .ConfigureAwait(false);
        var nginxPlugin = pluginsExecution.Error is null &&
            pluginsExecution.Command?.ExitCode == 0 &&
            CertbotOutputParser.HasNginxPlugin(pluginsExecution.Command.StandardOutput);

        var certificatesExecution = await executor.ExecuteAsync(
                ReadOnly("sudo", ["-n", "certbot", "certificates"]),
                cancellationToken)
            .ConfigureAwait(false);
        if (certificatesExecution.Error is not null)
        {
            return new CertbotCapability(
                ClassifyCertbotState(certificatesExecution.Error.Code, certificatesExecution.Error.Message),
                version,
                nginxPlugin,
                [],
                certificatesExecution.Error.Message);
        }

        var certificatesCommand = certificatesExecution.Command!;
        if (certificatesCommand.ExitCode != 0)
        {
            var detail = FirstUseful(certificatesCommand.StandardError, certificatesCommand.StandardOutput, "Certbot certificate inventory failed.");
            return new CertbotCapability(ClassifyCertbotState(null, detail), version, nginxPlugin, [], detail);
        }

        if (Encoding.UTF8.GetByteCount(certificatesCommand.StandardOutput) > _options.MaximumCertbotOutputBytes)
        {
            return new CertbotCapability(
                CertbotRuntimeState.OutputUnrecognized,
                version,
                nginxPlugin,
                [],
                "Certbot certificate output exceeded the safety limit.");
        }

        try
        {
            var managed = CertbotOutputParser.ParseCertificates(certificatesCommand.StandardOutput, _options.MaximumCertificates);
            return new CertbotCapability(CertbotRuntimeState.Available, version, nginxPlugin, managed);
        }
        catch (FormatException exception)
        {
            return new CertbotCapability(CertbotRuntimeState.OutputUnrecognized, version, nginxPlugin, [], exception.Message);
        }
    }

    private async Task<TlsCertificateInventoryResult> InspectAfterMutationAsync(
        ServerProfile profile,
        CancellationToken cancellationToken)
    {
        try
        {
            return await InspectAsync(profile, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new TlsCertificateInventoryResult(
                null,
                new RemoteError(RemoteErrorCode.AmbiguousState, "Post-mutation certificate inspection was cancelled."));
        }
    }

    private async Task<CertbotCapability> ProbeCertbotAfterMutationAsync(
        IRemoteCommandExecutor executor,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ProbeCertbotAsync(executor, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new CertbotCapability(
                CertbotRuntimeState.ProbeFailed,
                null,
                false,
                [],
                "Post-mutation Certbot verification was cancelled.");
        }
    }

    private async Task<CommandCheck> VerifyNginxAsync(
        IRemoteCommandExecutor executor,
        bool mutationHasStarted,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(
                executor,
                "sudo",
                ["-n", "nginx", "-t"],
                OperationRisk.ReadOnly,
                ambiguousOnTransport: mutationHasStarted,
                cancellationToken)
            .ConfigureAwait(false);

    private async Task<CommandCheck> ExecuteAsync(
        IRemoteCommandExecutor executor,
        string executable,
        IReadOnlyList<string> arguments,
        OperationRisk risk,
        bool ambiguousOnTransport,
        CancellationToken cancellationToken)
    {
        var execution = await executor.ExecuteAsync(
                new RemoteCommandSpec(executable, arguments, _options.CommandTimeout, risk, StableEnvironment),
                cancellationToken)
            .ConfigureAwait(false);
        if (execution.Error is not null)
        {
            var error = ambiguousOnTransport && IsPotentiallyAmbiguous(execution.Error.Code)
                ? new RemoteError(
                    RemoteErrorCode.AmbiguousState,
                    "The remote operation lost a reliable completion signal. Refresh certificate and nginx state before retrying.",
                    execution.Error.TechnicalDetails)
                : execution.Error;
            return new CommandCheck(false, error.Code == RemoteErrorCode.AmbiguousState, error.Message, error);
        }

        var command = execution.Command!;
        if (command.ExitCode == 0)
        {
            return new CommandCheck(true, false, "Remote command completed.", null);
        }

        var detail = FirstUseful(command.StandardError, command.StandardOutput, "Remote command failed.");
        var code = ClassifyFailure(detail);
        return new CommandCheck(false, false, detail, new RemoteError(code, detail));
    }

    private RemoteCommandSpec ReadOnly(string executable, IReadOnlyList<string> arguments) =>
        new(executable, arguments, _options.CommandTimeout, OperationRisk.ReadOnly, StableEnvironment);

    private TlsCertificateHealth ClassifyHealth(DateTimeOffset notBefore, DateTimeOffset notAfter, DateTimeOffset now)
    {
        if (now < notBefore)
        {
            return TlsCertificateHealth.NotYetValid;
        }

        if (now >= notAfter)
        {
            return TlsCertificateHealth.Expired;
        }

        return notAfter - now <= TimeSpan.FromDays(_options.ExpiringSoonDays)
            ? TlsCertificateHealth.ExpiringSoon
            : TlsCertificateHealth.Valid;
    }

    private static bool HasCertificateChanged(TlsCertificateInfo before, TlsCertificateInfo after) =>
        !string.Equals(before.FingerprintSha256, after.FingerprintSha256, StringComparison.OrdinalIgnoreCase) ||
        before.NotAfterUtc != after.NotAfterUtc;

    private static RemoteError? ValidateObtainRequest(CertbotObtainRequest request)
    {
        if (!request.TermsAccepted)
        {
            return new RemoteError(RemoteErrorCode.PathConflict, "The ACME subscriber terms must be explicitly accepted before obtaining a certificate.");
        }

        try
        {
            _ = NormalizeCertificateName(request.CertificateName);
        }
        catch (ArgumentException exception)
        {
            return new RemoteError(RemoteErrorCode.InvalidEndpoint, exception.Message);
        }

        if (request.Domains.Count == 0 || request.Domains.Count > 100)
        {
            return new RemoteError(RemoteErrorCode.InvalidEndpoint, "At least one and at most 100 domains are required.");
        }

        foreach (var domain in request.Domains)
        {
            var value = domain.Trim();
            if (value.StartsWith("*.", StringComparison.Ordinal) ||
                Uri.CheckHostName(value) != UriHostNameType.Dns ||
                value.IndexOfAny(['\r', '\n', '\0']) >= 0)
            {
                return new RemoteError(RemoteErrorCode.InvalidEndpoint, $"Domain '{value}' is not a supported DNS name for the nginx Certbot flow.");
            }
        }

        try
        {
            var address = new MailAddress(request.Email.Trim());
            if (!string.Equals(address.Address, request.Email.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return new RemoteError(RemoteErrorCode.InvalidEndpoint, "A single valid notification email address is required.");
            }
        }
        catch (FormatException)
        {
            return new RemoteError(RemoteErrorCode.InvalidEndpoint, "A valid notification email address is required.");
        }

        return null;
    }

    private static string NormalizeCertificateName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var trimmed = value.Trim();
        if (trimmed.Length > 253 ||
            trimmed.Contains('/') ||
            trimmed.Contains('\\') ||
            trimmed.IndexOfAny(['\r', '\n', '\0']) >= 0 ||
            trimmed.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Certificate name must be a compact Certbot lineage name without path separators or control characters.", nameof(value));
        }

        return trimmed;
    }

    private static string NormalizeAbsolutePath(string value, string parameterName)
    {
        if (!TryNormalizeAbsolutePath(value, out var normalized))
        {
            throw new ArgumentException("Certificate path must be an absolute remote path without control characters.", parameterName);
        }

        return normalized;
    }

    private static bool TryNormalizeAbsolutePath(string value, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;
        return normalized.StartsWith('/', StringComparison.Ordinal) &&
            normalized.Length <= 4096 &&
            normalized.IndexOfAny(['\r', '\n', '\0']) < 0;
    }

    private static TlsCertificateInfo Unreadable(
        string path,
        IReadOnlyCollection<string> privateKeyPaths,
        IReadOnlyCollection<string> siteNames,
        string? certbotName,
        string error) =>
        new(
            path,
            privateKeyPaths.Distinct(StringComparer.Ordinal).ToArray(),
            null,
            [],
            null,
            null,
            null,
            null,
            null,
            TlsCertificateHealth.Unreadable,
            siteNames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            certbotName,
            error);

    private static CertbotRuntimeState ClassifyCertbotState(RemoteErrorCode? code, string detail)
    {
        if (code == RemoteErrorCode.CommandNotFound)
        {
            return CertbotRuntimeState.CliMissing;
        }

        if (code == RemoteErrorCode.SudoRequired ||
            detail.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("sudoers", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("not allowed to run sudo", StringComparison.OrdinalIgnoreCase))
        {
            return CertbotRuntimeState.SudoRequired;
        }

        if (code == RemoteErrorCode.PermissionDenied || detail.Contains("permission denied", StringComparison.OrdinalIgnoreCase))
        {
            return CertbotRuntimeState.PermissionDenied;
        }

        return CertbotRuntimeState.ProbeFailed;
    }

    private static RemoteError CertbotUnavailable(CertbotCapability capability, bool requireNginxPlugin = false)
    {
        var message = capability.State == CertbotRuntimeState.Available && requireNginxPlugin && !capability.NginxPluginAvailable
            ? "Certbot is available, but the nginx plugin was not positively detected. Obtain is disabled."
            : $"Certbot mutation is unavailable because capability state is {capability.State}.";
        return new RemoteError(RemoteErrorCode.CapabilityUnavailable, message, capability.Detail);
    }

    private static RemoteErrorCode ClassifyFailure(string detail)
    {
        if (detail.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("sudoers", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.SudoRequired;
        }

        if (detail.Contains("permission denied", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.PermissionDenied;
        }

        if (detail.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("no such file", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.PathNotFound;
        }

        return RemoteErrorCode.CommandFailed;
    }

    private static bool IsPotentiallyAmbiguous(RemoteErrorCode code) =>
        code is RemoteErrorCode.NetworkInterrupted or RemoteErrorCode.CommandTimeout or RemoteErrorCode.OperationCancelled;

    private static string FirstUseful(string first, string second, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(first))
        {
            return first.Trim();
        }

        return !string.IsNullOrWhiteSpace(second) ? second.Trim() : fallback;
    }

    private static CertbotMutationResult Failure(RemoteError error) =>
        new(false, false, false, error.Message, error);

    private static CertbotMutationResult Ambiguous(string message) =>
        new(false, true, false, message, new RemoteError(RemoteErrorCode.AmbiguousState, message));

    private sealed class CertificateReference
    {
        public CertificateReference(string certificatePath) => CertificatePath = certificatePath;
        public string CertificatePath { get; }
        public HashSet<string> PrivateKeyPaths { get; } = new(StringComparer.Ordinal);
        public HashSet<string> SiteNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record CommandCheck(bool IsSuccess, bool Ambiguous, string Message, RemoteError? Error)
    {
        public CertbotMutationResult ToMutationResult(
            bool certificateChanged = false,
            TlsCertificateInfo? certificate = null) =>
            new(
                false,
                Ambiguous,
                certificateChanged,
                Message,
                Error,
                certificate);
    }
}

public sealed class AuditedTlsCertificateService : ITlsCertificateService
{
    private readonly ITlsCertificateService _inner;
    private readonly IOperationAudit _audit;

    public AuditedTlsCertificateService(ITlsCertificateService inner, IOperationAudit audit)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public Task<TlsCertificateInventoryResult> InspectAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default) =>
        _inner.InspectAsync(profile, cancellationToken);

    public async Task<CertbotMutationResult> RenewAsync(
        ServerProfile profile,
        string certificateName,
        string expectedCertificatePath,
        CancellationToken cancellationToken = default)
    {
        var result = await _inner.RenewAsync(profile, certificateName, expectedCertificatePath, cancellationToken).ConfigureAwait(false);
        await AuditAsync(profile, "renew", certificateName, OperationRisk.Destructive, result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<CertbotMutationResult> ObtainAsync(
        ServerProfile profile,
        CertbotObtainRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _inner.ObtainAsync(profile, request, cancellationToken).ConfigureAwait(false);
        await AuditAsync(profile, "obtain", request.CertificateName, OperationRisk.Mutating, result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task AuditAsync(
        ServerProfile profile,
        string action,
        string certificateName,
        OperationRisk risk,
        CertbotMutationResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            var outcome = result.IsSuccess
                ? OperationOutcome.Succeeded
                : result.AmbiguousState
                    ? OperationOutcome.Unknown
                    : OperationOutcome.Failed;
            var target = $"{profile.Username}@{profile.Host}:{profile.Port} certbot:{certificateName}";
            var entry = OperationAuditEntry.Create(
                "tls-certbot",
                $"Certbot {action} requested for certificate {certificateName}",
                risk,
                outcome,
                target);
            await _audit.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Audit persistence failure must not trigger a retry of a certificate mutation.
        }
    }
}
