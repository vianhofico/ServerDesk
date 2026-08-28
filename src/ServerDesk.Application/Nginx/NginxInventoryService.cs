using ServerDesk.Application.Remote;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Nginx;

public interface INginxInventoryService
{
    Task<NginxInventoryResult> InspectAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default);
}

public sealed class NginxInventoryService : INginxInventoryService
{
    private static readonly IReadOnlyDictionary<string, string> StableEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["LC_ALL"] = "C" };

    private readonly IRemoteCommandExecutorFactory _commandFactory;
    private readonly NginxInventoryOptions _options;

    public NginxInventoryService(
        IRemoteCommandExecutorFactory commandFactory,
        NginxInventoryOptions options)
    {
        _commandFactory = commandFactory ?? throw new ArgumentNullException(nameof(commandFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<NginxInventoryResult> InspectAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using var executor = _commandFactory.Create(profile);

        var versionExecution = await executor.ExecuteAsync(
                ReadOnly("nginx", ["-v"]),
                cancellationToken)
            .ConfigureAwait(false);
        if (versionExecution.Error is not null)
        {
            if (versionExecution.Error.Code == RemoteErrorCode.CommandNotFound)
            {
                return KnownState(NginxRuntimeState.CliMissing, null, versionExecution.Error.Message);
            }

            if (versionExecution.Error.Code == RemoteErrorCode.PermissionDenied)
            {
                return KnownState(NginxRuntimeState.PermissionDenied, null, versionExecution.Error.Message);
            }

            return new NginxInventoryResult(null, versionExecution.Error);
        }

        var versionCommand = versionExecution.Command!;
        var version = NginxConfigParser.ParseVersion(
            versionCommand.StandardOutput,
            versionCommand.StandardError);
        if (versionCommand.ExitCode != 0)
        {
            return KnownState(
                NginxRuntimeState.ProbeFailed,
                version,
                FirstUseful(versionCommand.StandardError, versionCommand.StandardOutput, "nginx version probe failed."));
        }

        var dumpExecution = await executor.ExecuteAsync(
                ReadOnly("nginx", ["-T"]),
                cancellationToken)
            .ConfigureAwait(false);
        if (dumpExecution.Error is not null)
        {
            if (dumpExecution.Error.Code == RemoteErrorCode.CommandNotFound)
            {
                return KnownState(NginxRuntimeState.CliMissing, version, dumpExecution.Error.Message);
            }

            if (dumpExecution.Error.Code == RemoteErrorCode.PermissionDenied)
            {
                return KnownState(NginxRuntimeState.PermissionDenied, version, dumpExecution.Error.Message);
            }

            return new NginxInventoryResult(null, dumpExecution.Error);
        }

        var command = dumpExecution.Command!;
        if (command.ExitCode != 0)
        {
            var detail = FirstUseful(command.StandardError, command.StandardOutput, "nginx configuration probe failed.");
            var state = detail.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
                ? NginxRuntimeState.PermissionDenied
                : NginxRuntimeState.InvalidConfiguration;
            return KnownState(state, version, detail, NginxConfigParser.Sanitize(command.StandardOutput));
        }

        try
        {
            var parsed = NginxConfigParser.Parse(command.StandardOutput, _options);
            return new NginxInventoryResult(
                new NginxInventorySnapshot(
                    NginxRuntimeState.Available,
                    version,
                    parsed.Sites,
                    parsed.Sources,
                    parsed.RawDump,
                    FirstUseful(command.StandardError, string.Empty, string.Empty)),
                null);
        }
        catch (FormatException exception)
        {
            return new NginxInventoryResult(
                null,
                new RemoteError(
                    RemoteErrorCode.ParseFailed,
                    "ServerDesk could not safely normalize the nginx configuration dump.",
                    exception.Message));
        }
    }

    private NginxInventoryResult KnownState(
        NginxRuntimeState state,
        string? version,
        string? detail,
        string rawDump = "") =>
        new(
            new NginxInventorySnapshot(
                state,
                version,
                [],
                [],
                rawDump,
                string.IsNullOrWhiteSpace(detail) ? null : NginxConfigParser.Sanitize(detail)),
            null);

    private RemoteCommandSpec ReadOnly(string executable, IReadOnlyList<string> arguments) =>
        new(
            executable,
            arguments,
            _options.CommandTimeout,
            OperationRisk.ReadOnly,
            StableEnvironment);

    private static string FirstUseful(string first, string second, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(first))
        {
            return NginxConfigParser.Sanitize(first.Trim());
        }

        return !string.IsNullOrWhiteSpace(second)
            ? NginxConfigParser.Sanitize(second.Trim())
            : fallback;
    }
}
