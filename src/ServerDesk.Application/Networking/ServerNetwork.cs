using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ServerDesk.Application.Remote;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Networking;

public sealed record ServerNetworkAddress(
    string Family,
    string Address,
    int PrefixLength,
    string Scope);

public sealed record ServerNetworkInterfaceInfo(
    string Name,
    string OperationalState,
    int? Mtu,
    string MacAddress,
    IReadOnlyList<ServerNetworkAddress> Addresses,
    long RxBytes,
    long TxBytes,
    double RxBytesPerSecond,
    double TxBytesPerSecond);

public sealed record ServerListeningSocketInfo(
    string Protocol,
    string State,
    string LocalAddress,
    int Port,
    int? ProcessId,
    string ProcessName,
    bool OwnerVisible);

public sealed record ServerNetworkSnapshotResult(
    IReadOnlyList<ServerNetworkInterfaceInfo> Interfaces,
    IReadOnlyList<ServerListeningSocketInfo> ListeningSockets,
    RemoteError? Error)
{
    public bool IsSuccess => Error is null;
}

public sealed record ServerNetworkOptions(TimeSpan SampleInterval)
{
    public static ServerNetworkOptions Default { get; } = new(TimeSpan.FromMilliseconds(600));

    public void Validate()
    {
        if (SampleInterval <= TimeSpan.Zero || SampleInterval > TimeSpan.FromSeconds(5))
        {
            throw new ArgumentOutOfRangeException(nameof(SampleInterval));
        }
    }
}

public interface IServerNetworkService
{
    Task<ServerNetworkSnapshotResult> InspectAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default);
}

public sealed class ServerNetworkService : IServerNetworkService
{
    private static readonly IReadOnlyDictionary<string, string> StableEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["LC_ALL"] = "C" };

    private readonly IRemoteCommandExecutorFactory _commandExecutorFactory;
    private readonly ServerNetworkOptions _options;

    public ServerNetworkService(
        IRemoteCommandExecutorFactory commandExecutorFactory,
        ServerNetworkOptions options)
    {
        _commandExecutorFactory = commandExecutorFactory ?? throw new ArgumentNullException(nameof(commandExecutorFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async Task<ServerNetworkSnapshotResult> InspectAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using var executor = _commandExecutorFactory.Create(profile);

        var addressExecution = await executor.ExecuteAsync(
                new RemoteCommandSpec(
                    "ip",
                    ["-j", "address", "show"],
                    TimeSpan.FromSeconds(15),
                    OperationRisk.ReadOnly,
                    StableEnvironment),
                cancellationToken)
            .ConfigureAwait(false);
        var addressError = ToReadError(addressExecution, "Unable to inspect network interfaces.");
        if (addressError is not null)
        {
            return new ServerNetworkSnapshotResult([], [], addressError);
        }

        var countersBeforeExecution = await ReadCountersAsync(executor, cancellationToken).ConfigureAwait(false);
        if (!countersBeforeExecution.IsSuccess)
        {
            return new ServerNetworkSnapshotResult([], [], countersBeforeExecution.Error);
        }

        await Task.Delay(_options.SampleInterval, cancellationToken).ConfigureAwait(false);

        var countersAfterExecution = await ReadCountersAsync(executor, cancellationToken).ConfigureAwait(false);
        if (!countersAfterExecution.IsSuccess)
        {
            return new ServerNetworkSnapshotResult([], [], countersAfterExecution.Error);
        }

        var socketsExecution = await executor.ExecuteAsync(
                new RemoteCommandSpec(
                    "ss",
                    ["-H", "-lntup"],
                    TimeSpan.FromSeconds(15),
                    OperationRisk.ReadOnly,
                    StableEnvironment),
                cancellationToken)
            .ConfigureAwait(false);
        var socketError = ToReadError(socketsExecution, "Unable to inspect listening sockets.");
        if (socketError is not null)
        {
            return new ServerNetworkSnapshotResult([], [], socketError);
        }

        try
        {
            var before = NetworkParser.ParseProcNetDev(countersBeforeExecution.Output!);
            var after = NetworkParser.ParseProcNetDev(countersAfterExecution.Output!);
            var interfaces = NetworkParser.ParseInterfaces(
                addressExecution.Command!.StandardOutput,
                before,
                after,
                _options.SampleInterval);
            var sockets = NetworkParser.ParseListeningSockets(socketsExecution.Command!.StandardOutput);
            return new ServerNetworkSnapshotResult(interfaces, sockets, null);
        }
        catch (FormatException exception)
        {
            return new ServerNetworkSnapshotResult(
                [],
                [],
                new RemoteError(
                    RemoteErrorCode.ParseFailed,
                    "ServerDesk could not parse remote network output.",
                    exception.Message));
        }
    }

    private static async Task<CounterExecution> ReadCountersAsync(
        IRemoteCommandExecutor executor,
        CancellationToken cancellationToken)
    {
        var execution = await executor.ExecuteAsync(
                new RemoteCommandSpec(
                    "cat",
                    ["/proc/net/dev"],
                    TimeSpan.FromSeconds(10),
                    OperationRisk.ReadOnly,
                    StableEnvironment),
                cancellationToken)
            .ConfigureAwait(false);
        var error = ToReadError(execution, "Unable to read network counters.");
        return error is null
            ? new CounterExecution(execution.Command!.StandardOutput, null)
            : new CounterExecution(null, error);
    }

    private static RemoteError? ToReadError(RemoteExecutionResult execution, string fallback)
    {
        if (execution.Error is not null)
        {
            return execution.Error;
        }

        var command = execution.Command!;
        if (command.ExitCode == 0)
        {
            return null;
        }

        var detail = FirstUseful(command.StandardError, command.StandardOutput, fallback);
        var code = detail.Contains("command not found", StringComparison.OrdinalIgnoreCase) ||
                   detail.Contains("not found", StringComparison.OrdinalIgnoreCase)
            ? RemoteErrorCode.CommandNotFound
            : detail.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
              detail.Contains("operation not permitted", StringComparison.OrdinalIgnoreCase)
                ? RemoteErrorCode.PermissionDenied
                : RemoteErrorCode.CommandFailed;
        return new RemoteError(code, detail);
    }

    private static string FirstUseful(string first, string second, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(first))
        {
            return first.Trim();
        }

        return !string.IsNullOrWhiteSpace(second) ? second.Trim() : fallback;
    }

    private sealed record CounterExecution(string? Output, RemoteError? Error)
    {
        public bool IsSuccess => Error is null;
    }
}

public sealed record NetworkCounter(long RxBytes, long TxBytes);

public static partial class NetworkParser
{
    [GeneratedRegex(
        @"^(?<proto>\S+)\s+(?<state>\S+)\s+\S+\s+\S+\s+(?<local>\S+)\s+\S+(?:\s+(?<owner>.*))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SocketRowRegex();

    [GeneratedRegex(
        "users:\\\(\\\\(\\\"(?<name>[^\\\"]+)\\\",pid=(?<pid>\\d+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SocketOwnerRegex();

    public static IReadOnlyDictionary<string, NetworkCounter> ParseProcNetDev(string output)
    {
        var result = new Dictionary<string, NetworkCounter>(StringComparer.Ordinal);
        foreach (var raw in output.Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = raw.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var name = raw[..colon].Trim();
            var values = raw[(colon + 1)..]
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (values.Length < 16 ||
                !long.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var rx) ||
                !long.TryParse(values[8], NumberStyles.None, CultureInfo.InvariantCulture, out var tx) ||
                rx < 0 ||
                tx < 0)
            {
                throw new FormatException($"Malformed /proc/net/dev row: '{raw}'.");
            }

            result[name] = new NetworkCounter(rx, tx);
        }

        return result;
    }

    public static IReadOnlyList<ServerNetworkInterfaceInfo> ParseInterfaces(
        string json,
        IReadOnlyDictionary<string, NetworkCounter> before,
        IReadOnlyDictionary<string, NetworkCounter> after,
        TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("ip JSON root is not an array.");
            }

            var result = new List<ServerNetworkInterfaceInfo>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                var name = RequiredString(element, "ifname");
                var state = OptionalString(element, "operstate");
                var mtu = OptionalInt32(element, "mtu");
                var mac = OptionalString(element, "address");
                var addresses = ParseAddresses(element);
                before.TryGetValue(name, out var beforeCounter);
                after.TryGetValue(name, out var afterCounter);
                var rx = afterCounter?.RxBytes ?? beforeCounter?.RxBytes ?? 0;
                var tx = afterCounter?.TxBytes ?? beforeCounter?.TxBytes ?? 0;
                var rxRate = CalculateRate(beforeCounter?.RxBytes, afterCounter?.RxBytes, interval);
                var txRate = CalculateRate(beforeCounter?.TxBytes, afterCounter?.TxBytes, interval);
                result.Add(new ServerNetworkInterfaceInfo(
                    name,
                    state.Length == 0 ? "UNKNOWN" : state,
                    mtu,
                    mac,
                    addresses,
                    rx,
                    tx,
                    rxRate,
                    txRate));
            }

            return result.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
        }
        catch (JsonException exception)
        {
            throw new FormatException("ip returned invalid JSON.", exception);
        }
    }

    public static IReadOnlyList<ServerListeningSocketInfo> ParseListeningSockets(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var result = new List<ServerListeningSocketInfo>();
        foreach (var raw in output.Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = SocketRowRegex().Match(raw.Trim());
            if (!match.Success)
            {
                throw new FormatException($"Malformed ss row: '{raw}'.");
            }

            var endpoint = ParseLocalEndpoint(match.Groups["local"].Value);
            var ownerText = match.Groups["owner"].Value;
            var ownerMatch = SocketOwnerRegex().Match(ownerText);
            int? pid = null;
            var processName = string.Empty;
            if (ownerMatch.Success)
            {
                processName = ownerMatch.Groups["name"].Value;
                if (int.TryParse(
                        ownerMatch.Groups["pid"].Value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var parsedPid))
                {
                    pid = parsedPid;
                }
            }

            result.Add(new ServerListeningSocketInfo(
                match.Groups["proto"].Value,
                match.Groups["state"].Value,
                endpoint.Address,
                endpoint.Port,
                pid,
                processName,
                pid.HasValue));
        }

        return result
            .OrderBy(item => item.Port)
            .ThenBy(item => item.Protocol, StringComparer.Ordinal)
            .ThenBy(item => item.LocalAddress, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<ServerNetworkAddress> ParseAddresses(JsonElement element)
    {
        if (!element.TryGetProperty("addr_info", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<ServerNetworkAddress>();
        foreach (var value in values.EnumerateArray())
        {
            var local = OptionalString(value, "local");
            if (local.Length == 0)
            {
                continue;
            }

            result.Add(new ServerNetworkAddress(
                OptionalString(value, "family"),
                local,
                OptionalInt32(value, "prefixlen") ?? 0,
                OptionalString(value, "scope")));
        }

        return result;
    }

    private static double CalculateRate(long? before, long? after, TimeSpan interval)
    {
        if (!before.HasValue || !after.HasValue || after.Value < before.Value)
        {
            return 0;
        }

        return (after.Value - before.Value) / interval.TotalSeconds;
    }

    private static (string Address, int Port) ParseLocalEndpoint(string value)
    {
        var separator = value.LastIndexOf(':');
        if (separator < 0 || separator == value.Length - 1 ||
            !int.TryParse(value[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 0 or > 65535)
        {
            throw new FormatException($"Invalid local socket endpoint '{value}'.");
        }

        var address = value[..separator];
        if (address.StartsWith("[", StringComparison.Ordinal) && address.EndsWith("]", StringComparison.Ordinal))
        {
            address = address[1..^1];
        }

        return (address.Length == 0 ? "*" : address, port);
    }

    private static string RequiredString(JsonElement element, string property)
    {
        var value = OptionalString(element, property);
        return value.Length > 0
            ? value
            : throw new FormatException($"ip interface is missing '{property}'.");
    }

    private static string OptionalString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.ToString();
    }

    private static int? OptionalInt32(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return null;
    }
}
