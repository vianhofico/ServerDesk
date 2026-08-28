using System.Collections.Concurrent;
using ServerDesk.Application.Remote;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Capabilities;

public enum CapabilityStatus
{
    Available,
    Unavailable,
    PermissionDenied,
    Unknown,
}

public sealed record CapabilityState(
    CapabilityStatus Status,
    string? Version = null,
    string? Detail = null)
{
    public static CapabilityState Available(string? version = null, string? detail = null) =>
        new(CapabilityStatus.Available, version, detail);

    public static CapabilityState Unavailable(string detail) =>
        new(CapabilityStatus.Unavailable, null, detail);

    public static CapabilityState PermissionDenied(string detail) =>
        new(CapabilityStatus.PermissionDenied, null, detail);

    public static CapabilityState Unknown(string detail) =>
        new(CapabilityStatus.Unknown, null, detail);
}

public sealed record SudoCapabilityState(
    CapabilityStatus Status,
    bool? Passwordless,
    string? Version = null,
    string? Detail = null);

public sealed record LinuxIdentitySnapshot(
    string OsId,
    string OsName,
    string? VersionId,
    string? PrettyName,
    string Architecture,
    string KernelVersion,
    string CurrentUser,
    int? UserId,
    bool IsRoot)
{
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(PrettyName)
            ? PrettyName
            : !string.IsNullOrWhiteSpace(VersionId)
                ? $"{OsName} {VersionId}"
                : OsName;
}

public sealed record ServerCapabilitySnapshot(
    Guid ServerProfileId,
    DateTimeOffset CapturedAtUtc,
    LinuxIdentitySnapshot Identity,
    CapabilityState Systemd,
    CapabilityState Docker,
    CapabilityState DockerCompose,
    CapabilityState Nginx,
    CapabilityState Apache,
    CapabilityState Git,
    CapabilityState Ufw,
    CapabilityState Firewalld,
    CapabilityState PostgreSql,
    CapabilityState MySql,
    CapabilityState Redis,
    SudoCapabilityState Sudo)
{
    public IReadOnlyList<string> AvailableHighlights
    {
        get
        {
            var highlights = new List<string>();
            Add(highlights, "systemd", Systemd);
            Add(highlights, "Docker", Docker);
            Add(highlights, "Compose", DockerCompose);
            Add(highlights, "nginx", Nginx);
            Add(highlights, "Apache", Apache);
            Add(highlights, "Git", Git);
            Add(highlights, "UFW", Ufw);
            Add(highlights, "firewalld", Firewalld);
            Add(highlights, "PostgreSQL", PostgreSql);
            Add(highlights, "MySQL", MySql);
            Add(highlights, "Redis", Redis);
            return highlights;
        }
    }

    private static void Add(ICollection<string> target, string label, CapabilityState state)
    {
        if (state.Status == CapabilityStatus.Available)
        {
            target.Add(label);
        }
    }
}

public sealed record ServerCapabilityOptions(TimeSpan CacheDuration)
{
    public static ServerCapabilityOptions Default { get; } = new(TimeSpan.FromMinutes(5));
}

public interface IServerCapabilityService
{
    ValueTask<ServerCapabilitySnapshot> GetAsync(
        ServerProfile profile,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);

    void Invalidate(Guid serverProfileId);
}

public sealed class ServerCapabilityService : IServerCapabilityService, IAsyncDisposable
{
    private static readonly CapabilityProbe[] Probes =
    [
        new("systemd", ["systemctl"], "systemctl", ["--version"]),
        new("docker", ["docker"], "docker", ["--version"]),
        new("nginx", ["nginx"], "nginx", ["-v"], PreferStandardError: true),
        new("apache", ["apache2", "httpd"], null, ["-v"]),
        new("git", ["git"], "git", ["--version"]),
        new("ufw", ["ufw"], "ufw", ["--version"]),
        new("firewalld", ["firewall-cmd"], "firewall-cmd", ["--version"]),
        new("postgresql", ["psql"], "psql", ["--version"]),
        new("mysql", ["mysql", "mariadb"], null, ["--version"]),
        new("redis", ["redis-server"], "redis-server", ["--version"]),
    ];

    private readonly IRemoteCommandExecutorFactory _executorFactory;
    private readonly ServerCapabilityOptions _options;
    private readonly ConcurrentDictionary<Guid, CacheEntry> _cache = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();
    private bool _disposed;

    public ServerCapabilityService(
        IRemoteCommandExecutorFactory executorFactory,
        ServerCapabilityOptions options)
    {
        _executorFactory = executorFactory ?? throw new ArgumentNullException(nameof(executorFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async ValueTask<ServerCapabilitySnapshot> GetAsync(
        ServerProfile profile,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!forceRefresh && TryGetFresh(profile.Id, out var cached))
        {
            return cached;
        }

        var gate = _locks.GetOrAdd(profile.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && TryGetFresh(profile.Id, out cached))
            {
                return cached;
            }

            await using var executor = _executorFactory.Create(profile);
            var snapshot = await ScanAsync(profile, executor, cancellationToken).ConfigureAwait(false);
            _cache[profile.Id] = new CacheEntry(snapshot, DateTimeOffset.UtcNow + _options.CacheDuration);
            return snapshot;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Invalidate(Guid serverProfileId)
    {
        _cache.TryRemove(serverProfileId, out _);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _cache.Clear();
        foreach (var gate in _locks.Values)
        {
            gate.Dispose();
        }

        _locks.Clear();
        return ValueTask.CompletedTask;
    }

    private bool TryGetFresh(Guid serverProfileId, out ServerCapabilitySnapshot snapshot)
    {
        if (_cache.TryGetValue(serverProfileId, out var entry) && entry.ExpiresAtUtc > DateTimeOffset.UtcNow)
        {
            snapshot = entry.Snapshot;
            return true;
        }

        _cache.TryRemove(serverProfileId, out _);
        snapshot = null!;
        return false;
    }

    private static async ValueTask<ServerCapabilitySnapshot> ScanAsync(
        ServerProfile profile,
        IRemoteCommandExecutor executor,
        CancellationToken cancellationToken)
    {
        var osRelease = await RunAsync(
            executor,
            RemoteCommandSpec.ReadOnly("/bin/sh", "-lc", "cat /etc/os-release 2>/dev/null"),
            cancellationToken).ConfigureAwait(false);
        var osValues = osRelease.IsSuccessfulCommand
            ? OsReleaseParser.Parse(osRelease.Output)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var architecture = await ReadSingleValueAsync(executor, "uname -m", "unknown", cancellationToken)
            .ConfigureAwait(false);
        var kernel = await ReadSingleValueAsync(executor, "uname -r", "unknown", cancellationToken)
            .ConfigureAwait(false);
        var currentUser = await ReadSingleValueAsync(executor, "id -un", profile.Username, cancellationToken)
            .ConfigureAwait(false);
        var uidText = await ReadSingleValueAsync(executor, "id -u", string.Empty, cancellationToken)
            .ConfigureAwait(false);
        int? uid = int.TryParse(uidText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsedUid)
            ? parsedUid
            : null;
        var identity = new LinuxIdentitySnapshot(
            ValueOr(osValues, "ID", "unknown"),
            ValueOr(osValues, "NAME", "Linux"),
            ValueOrNull(osValues, "VERSION_ID"),
            ValueOrNull(osValues, "PRETTY_NAME"),
            architecture,
            kernel,
            currentUser,
            uid,
            uid == 0);

        var results = new Dictionary<string, CapabilityState>(StringComparer.Ordinal);
        foreach (var probe in Probes)
        {
            results[probe.Key] = await ProbeCapabilityAsync(executor, probe, cancellationToken).ConfigureAwait(false);
        }

        if (results["docker"].Status == CapabilityStatus.Available)
        {
            results["docker"] = await ProbeDockerRuntimeAsync(
                    executor,
                    results["docker"].Version,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var compose = results["docker"].Status == CapabilityStatus.Available
            ? await ProbeDockerComposeAsync(executor, cancellationToken).ConfigureAwait(false)
            : CapabilityState.Unavailable("Docker runtime is not usable, so Docker Compose cannot be used safely.");
        var sudo = await ProbeSudoAsync(executor, cancellationToken).ConfigureAwait(false);

        return new ServerCapabilitySnapshot(
            profile.Id,
            DateTimeOffset.UtcNow,
            identity,
            results["systemd"],
            results["docker"],
            compose,
            results["nginx"],
            results["apache"],
            results["git"],
            results["ufw"],
            results["firewalld"],
            results["postgresql"],
            results["mysql"],
            results["redis"],
            sudo);
    }

    private static async ValueTask<CapabilityState> ProbeCapabilityAsync(
        IRemoteCommandExecutor executor,
        CapabilityProbe probe,
        CancellationToken cancellationToken)
    {
        string? resolved = null;
        foreach (var candidate in probe.CommandNames)
        {
            var lookup = await RunShellAsync(executor, $"command -v {candidate} >/dev/null 2>&1", cancellationToken)
                .ConfigureAwait(false);
            if (lookup.ExecutionError is not null)
            {
                return CapabilityState.Unknown(lookup.ExecutionError.Message);
            }

            if (lookup.ExitCode == 0)
            {
                resolved = candidate;
                break;
            }
        }

        if (resolved is null)
        {
            return CapabilityState.Unavailable($"{string.Join("/", probe.CommandNames)} was not found in PATH.");
        }

        var executable = probe.VersionExecutable ?? resolved;
        var version = await RunAsync(
            executor,
            new RemoteCommandSpec(executable, probe.VersionArguments, TimeSpan.FromSeconds(8)),
            cancellationToken).ConfigureAwait(false);
        if (version.ExecutionError is not null)
        {
            return CapabilityState.Unknown(version.ExecutionError.Message);
        }

        if (version.ExitCode == 0)
        {
            var source = probe.PreferStandardError && !string.IsNullOrWhiteSpace(version.StandardError)
                ? version.StandardError
                : !string.IsNullOrWhiteSpace(version.Output)
                    ? version.Output
                    : version.StandardError;
            return CapabilityState.Available(FirstNonEmptyLine(source));
        }

        if (LooksLikePermissionDenied(version.StandardError, version.Output))
        {
            return CapabilityState.PermissionDenied($"{resolved} exists but cannot be executed by the current user.");
        }

        return CapabilityState.Unknown(
            $"{resolved} exists but its version probe returned exit code {version.ExitCode}.");
    }

    private static async ValueTask<CapabilityState> ProbeDockerRuntimeAsync(
        IRemoteCommandExecutor executor,
        string? cliVersion,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            executor,
            new RemoteCommandSpec("docker", ["version", "--format", "{{json .}}"], TimeSpan.FromSeconds(8)),
            cancellationToken).ConfigureAwait(false);
        if (result.ExecutionError is not null)
        {
            return CapabilityState.Unknown(result.ExecutionError.Message);
        }

        if (result.ExitCode == 0)
        {
            return CapabilityState.Available(
                cliVersion,
                "Docker CLI can communicate with the Docker daemon for the current user.");
        }

        var text = $"{result.StandardError}\n{result.Output}";
        if (LooksLikePermissionDenied(result.StandardError, result.Output))
        {
            return CapabilityState.PermissionDenied(
                "Docker CLI is installed, but the current user cannot access the Docker daemon.");
        }

        if (text.Contains("cannot connect to the docker daemon", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("is the docker daemon running", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("docker daemon is not running", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("error during connect", StringComparison.OrdinalIgnoreCase))
        {
            return CapabilityState.Unavailable(
                "Docker CLI is installed, but the Docker daemon is unavailable.");
        }

        if (text.Contains("minimum supported api version", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("api version is too old", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("client version", StringComparison.OrdinalIgnoreCase) &&
            text.Contains("too new", StringComparison.OrdinalIgnoreCase))
        {
            return CapabilityState.Unavailable(
                "The installed Docker client/server API combination is unsupported.");
        }

        return CapabilityState.Unknown(
            $"Docker runtime probe returned exit code {result.ExitCode} with an unrecognized response.");
    }

    private static async ValueTask<CapabilityState> ProbeDockerComposeAsync(
        IRemoteCommandExecutor executor,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            executor,
            new RemoteCommandSpec("docker", ["compose", "version", "--short"], TimeSpan.FromSeconds(8)),
            cancellationToken).ConfigureAwait(false);
        if (result.ExecutionError is not null)
        {
            return CapabilityState.Unknown(result.ExecutionError.Message);
        }

        if (result.ExitCode == 0)
        {
            return CapabilityState.Available(FirstNonEmptyLine(result.Output));
        }

        if (LooksLikePermissionDenied(result.StandardError, result.Output))
        {
            return CapabilityState.PermissionDenied("Docker Compose exists but the current user cannot execute it.");
        }

        return result.StandardError.Contains("not a docker command", StringComparison.OrdinalIgnoreCase) ||
               result.StandardError.Contains("unknown command", StringComparison.OrdinalIgnoreCase)
            ? CapabilityState.Unavailable("The Docker CLI does not provide the Compose plugin.")
            : CapabilityState.Unknown($"Docker Compose probe returned exit code {result.ExitCode}.");
    }

    private static async ValueTask<SudoCapabilityState> ProbeSudoAsync(
        IRemoteCommandExecutor executor,
        CancellationToken cancellationToken)
    {
        var exists = await RunShellAsync(executor, "command -v sudo >/dev/null 2>&1", cancellationToken)
            .ConfigureAwait(false);
        if (exists.ExecutionError is not null)
        {
            return new SudoCapabilityState(CapabilityStatus.Unknown, null, Detail: exists.ExecutionError.Message);
        }

        if (exists.ExitCode != 0)
        {
            return new SudoCapabilityState(
                CapabilityStatus.Unavailable,
                null,
                Detail: "sudo was not found in PATH.");
        }

        var versionResult = await RunAsync(
            executor,
            new RemoteCommandSpec("sudo", ["--version"], TimeSpan.FromSeconds(8)),
            cancellationToken).ConfigureAwait(false);
        var version = versionResult.ExitCode == 0 ? FirstNonEmptyLine(versionResult.Output) : null;
        var nonInteractive = await RunAsync(
            executor,
            new RemoteCommandSpec("sudo", ["-n", "true"], TimeSpan.FromSeconds(8)),
            cancellationToken).ConfigureAwait(false);
        if (nonInteractive.ExecutionError is not null)
        {
            return new SudoCapabilityState(
                CapabilityStatus.Unknown,
                null,
                version,
                nonInteractive.ExecutionError.Message);
        }

        if (nonInteractive.ExitCode == 0)
        {
            return new SudoCapabilityState(
                CapabilityStatus.Available,
                true,
                version,
                "Passwordless non-interactive sudo is available.");
        }

        var text = $"{nonInteractive.StandardError}\n{nonInteractive.Output}";
        if (text.Contains("password is required", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("a password is required", StringComparison.OrdinalIgnoreCase))
        {
            return new SudoCapabilityState(
                CapabilityStatus.Available,
                false,
                version,
                "sudo is available but requires interactive authentication.");
        }

        if (text.Contains("not allowed to run sudo", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("may not run sudo", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("is not in the sudoers", StringComparison.OrdinalIgnoreCase))
        {
            return new SudoCapabilityState(
                CapabilityStatus.PermissionDenied,
                false,
                version,
                "The current user is not allowed to use sudo.");
        }

        return new SudoCapabilityState(
            CapabilityStatus.Unknown,
            null,
            version,
            $"sudo -n true returned exit code {nonInteractive.ExitCode} with an unrecognized response.");
    }

    private static async ValueTask<string> ReadSingleValueAsync(
        IRemoteCommandExecutor executor,
        string script,
        string fallback,
        CancellationToken cancellationToken)
    {
        var result = await RunShellAsync(executor, script, cancellationToken).ConfigureAwait(false);
        return result.IsSuccessfulCommand && !string.IsNullOrWhiteSpace(result.Output)
            ? FirstNonEmptyLine(result.Output) ?? fallback
            : fallback;
    }

    private static ValueTask<ProbeExecution> RunShellAsync(
        IRemoteCommandExecutor executor,
        string script,
        CancellationToken cancellationToken) =>
        RunAsync(
            executor,
            new RemoteCommandSpec("/bin/sh", ["-lc", script], TimeSpan.FromSeconds(8)),
            cancellationToken);

    private static async ValueTask<ProbeExecution> RunAsync(
        IRemoteCommandExecutor executor,
        RemoteCommandSpec command,
        CancellationToken cancellationToken)
    {
        var result = await executor.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.Error is not null)
        {
            return new ProbeExecution(-1, string.Empty, string.Empty, result.Error);
        }

        var completed = result.Command!;
        return new ProbeExecution(
            completed.ExitCode,
            completed.StandardOutput,
            completed.StandardError,
            null);
    }

    private static bool LooksLikePermissionDenied(params string[] values) =>
        values.Any(value =>
            value.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("operation not permitted", StringComparison.OrdinalIgnoreCase));

    private static string? FirstNonEmptyLine(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

    private static string ValueOr(IReadOnlyDictionary<string, string> values, string key, string fallback) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    private static string? ValueOrNull(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private sealed record CapabilityProbe(
        string Key,
        IReadOnlyList<string> CommandNames,
        string? VersionExecutable,
        IReadOnlyList<string> VersionArguments,
        bool PreferStandardError = false);

    private sealed record ProbeExecution(
        int ExitCode,
        string Output,
        string StandardError,
        RemoteError? ExecutionError)
    {
        public bool IsSuccessfulCommand => ExecutionError is null && ExitCode == 0;
    }

    private sealed record CacheEntry(ServerCapabilitySnapshot Snapshot, DateTimeOffset ExpiresAtUtc);
}

public static class OsReleaseParser
{
    public static IReadOnlyDictionary<string, string> Parse(string content)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(content))
        {
            return values;
        }

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            if (!IsValidKey(key))
            {
                continue;
            }

            var value = line[(separator + 1)..].Trim();
            values[key] = DecodeValue(value);
        }

        return values;
    }

    private static string DecodeValue(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            var quote = value[0];
            value = value[1..^1];
            if (quote == '\'')
            {
                return value;
            }
        }

        var result = new System.Text.StringBuilder(value.Length);
        var escaped = false;
        foreach (var character in value)
        {
            if (escaped)
            {
                result.Append(character);
                escaped = false;
            }
            else if (character == '\\')
            {
                escaped = true;
            }
            else
            {
                result.Append(character);
            }
        }

        if (escaped)
        {
            result.Append('\\');
        }

        return result.ToString();
    }

    private static bool IsValidKey(string key) =>
        key.Length > 0 && key.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
}
