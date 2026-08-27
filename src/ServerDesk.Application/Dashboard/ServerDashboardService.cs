using System.Diagnostics;
using ServerDesk.Application.Remote;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Dashboard;

public sealed class ServerDashboardService : IServerDashboardService
{
    private const string FirstSampleScript =
        "cat /proc/stat 2>/dev/null; printf '\n__SD_NET__\n'; cat /proc/net/dev 2>/dev/null";

    private const string SecondSampleScript =
        "cat /proc/stat 2>/dev/null; " +
        "printf '\n__SD_NET__\n'; cat /proc/net/dev 2>/dev/null; " +
        "printf '\n__SD_LOAD__\n'; cat /proc/loadavg 2>/dev/null; " +
        "printf '\n__SD_UPTIME__\n'; cat /proc/uptime 2>/dev/null; " +
        "printf '\n__SD_MEM__\n'; cat /proc/meminfo 2>/dev/null; " +
        "printf '\n__SD_DF__\n'; df -P -T -B1 2>/dev/null";

    private readonly IRemoteCommandExecutorFactory _executorFactory;
    private readonly ServerDashboardOptions _options;

    public ServerDashboardService(
        IRemoteCommandExecutorFactory executorFactory,
        ServerDashboardOptions options)
    {
        _executorFactory = executorFactory ?? throw new ArgumentNullException(nameof(executorFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async ValueTask<ServerDashboardSnapshot> GetAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using var executor = _executorFactory.Create(profile);

        var first = await ExecuteAsync(executor, FirstSampleScript, cancellationToken).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        await Task.Delay(_options.SamplingInterval, cancellationToken).ConfigureAwait(false);
        var second = await ExecuteAsync(executor, SecondSampleScript, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        return LinuxDashboardParser.ParseSnapshot(
            profile.Id,
            DateTimeOffset.UtcNow,
            first,
            second,
            stopwatch.Elapsed,
            _options);
    }

    private static async Task<string> ExecuteAsync(
        IRemoteCommandExecutor executor,
        string script,
        CancellationToken cancellationToken)
    {
        var result = await executor.ExecuteAsync(
            new RemoteCommandSpec(
                "/bin/sh",
                ["-lc", script],
                TimeSpan.FromSeconds(10)),
            cancellationToken).ConfigureAwait(false);

        if (result.Error is not null)
        {
            throw new ServerDashboardException(result.Error);
        }

        return result.Command?.StandardOutput ?? string.Empty;
    }
}
