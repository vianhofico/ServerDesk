using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ServerDesk.Application.Docker;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class DockerContainerDiagnosticsWindow : Window
{
    private readonly IDockerContainerDiagnosticsService _service;
    private readonly ServerProfile _profile;
    private readonly DockerContainerInfo _container;
    private readonly bool _initiallyConnected;
    private readonly DockerLogRetentionBuffer _logBuffer;
    private CancellationTokenSource? _detailsCancellation;
    private CancellationTokenSource? _statsCancellation;
    private CancellationTokenSource? _logsCancellation;
    private bool _statsPolling;
    private bool _logsFollowing;
    private bool _closed;
    private string? _lastLogTimestampToken;

    public DockerContainerDiagnosticsWindow(
        IDockerContainerDiagnosticsService service,
        ServerProfile profile,
        DockerContainerInfo container,
        bool initiallyConnected)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _container = container ?? throw new ArgumentNullException(nameof(container));
        _initiallyConnected = initiallyConnected;
        _logBuffer = new DockerLogRetentionBuffer(_service.Options.MaxRetainedLogRows);

        InitializeComponent();
        TitleText.Text = $"Docker · {_container.Name}";
        IdentityText.Text = $"{_profile.Username}@{_profile.Host}:{_profile.Port} · {_container.Id}";
        LogStreamBox.ItemsSource = new[] { "All", DockerLogStream.Stdout.ToString(), DockerLogStream.Stderr.ToString() };
        LogStreamBox.SelectedIndex = 0;
        StatusText.Text = initiallyConnected
            ? "Initial: loading container diagnostics and verified state."
            : "Disconnected: connect the server before reading or managing this container.";
        FooterText.Text = "Diagnostics plus confirmed container actions. Secret-like environment values are discarded and shown redacted.";
        SetManagementButtonsEnabled(false);
    }

    private async void WindowOnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_initiallyConnected)
        {
            return;
        }

        await RefreshDetailsAsync();
        await RefreshStatsAsync();
        await RefreshLogsAsync();
    }

    private void WindowOnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        CancelAll();
    }

    private async void RefreshDetailsOnClick(object sender, RoutedEventArgs e) => await RefreshDetailsAsync();

    private async void RefreshStatsOnClick(object sender, RoutedEventArgs e) => await RefreshStatsAsync();

    private void StartStatsOnClick(object sender, RoutedEventArgs e) => StartStatsPolling();

    private void PauseStatsOnClick(object sender, RoutedEventArgs e)
    {
        _statsPolling = false;
        CancelStats();
        StatusText.Text = "Stats paused: the last normalized sample remains visible.";
    }

    private async void RefreshLogsOnClick(object sender, RoutedEventArgs e) => await RefreshLogsAsync();

    private async void FollowLogsOnClick(object sender, RoutedEventArgs e)
    {
        if (_logBuffer.Entries.Count == 0)
        {
            await RefreshLogsAsync();
        }

        StartLogFollowing();
    }

    private void PauseLogsOnClick(object sender, RoutedEventArgs e)
    {
        _logsFollowing = false;
        CancelLogs();
        StatusText.Text = $"Logs paused: {_logBuffer.Entries.Count:N0} row(s) retained.";
    }

    private void CancelAllOnClick(object sender, RoutedEventArgs e)
    {
        CancelAll();
        StatusText.Text = "Cancelled: active Docker work was stopped. If a mutation was in flight, refresh state before deciding whether to retry.";
    }

    private void LogFilterOnChanged(object sender, TextChangedEventArgs e) => ApplyLogFilter();

    private void LogFilterOnSelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyLogFilter();

    private async Task RefreshDetailsAsync()
    {
        if (!CanRead())
        {
            return;
        }

        CancelDetails();
        _detailsCancellation = new CancellationTokenSource();
        var source = _detailsCancellation;
        StatusText.Text = "Loading details: reading structured Docker inspect output…";
        try
        {
            var result = await _service.InspectAsync(_profile, _container.Id, source.Token);
            if (!result.IsSuccess)
            {
                ApplyError("Details", result.Error!);
                return;
            }

            ApplyDetails(result.Details!);
            StatusText.Text = "Details loaded. Secret-like environment values remain redacted.";
        }
        catch (OperationCanceledException)
        {
            if (!_closed)
            {
                StatusText.Text = "Cancelled: container details read stopped.";
            }
        }
        finally
        {
            DisposeIfCurrent(ref _detailsCancellation, source);
        }
    }

    private async Task RefreshStatsAsync()
    {
        if (!CanRead())
        {
            return;
        }

        CancelStats();
        _statsCancellation = new CancellationTokenSource();
        var source = _statsCancellation;
        StatusText.Text = "Loading stats: requesting a one-shot Docker stats sample…";
        try
        {
            var result = await _service.ReadStatsAsync(_profile, _container.Id, source.Token);
            if (!result.IsSuccess)
            {
                ApplyError("Stats", result.Error!);
                return;
            }

            ApplyStats(result.Stats!);
            StatusText.Text = "Stats loaded: one normalized sample captured.";
        }
        catch (OperationCanceledException)
        {
            if (!_closed)
            {
                StatusText.Text = "Cancelled: stats read stopped.";
            }
        }
        finally
        {
            DisposeIfCurrent(ref _statsCancellation, source);
        }
    }

    private void StartStatsPolling()
    {
        if (!CanRead())
        {
            return;
        }

        CancelStats();
        _statsPolling = true;
        _statsCancellation = new CancellationTokenSource();
        var source = _statsCancellation;
        StatusText.Text = $"Stats polling: every {_service.Options.StatsPollInterval.TotalSeconds:0.##}s. Pause or Cancel at any time.";
        _ = PollStatsAsync(source);
    }

    private async Task PollStatsAsync(CancellationTokenSource source)
    {
        var token = source.Token;
        try
        {
            while (_statsPolling && !_closed && !token.IsCancellationRequested)
            {
                var result = await _service.ReadStatsAsync(_profile, _container.Id, token);
                if (!result.IsSuccess)
                {
                    if (result.Error?.Code == RemoteErrorCode.OperationCancelled && token.IsCancellationRequested)
                    {
                        break;
                    }

                    _statsPolling = false;
                    ApplyError("Stats", result.Error!);
                    break;
                }

                ApplyStats(result.Stats!);
                StatusText.Text = $"Stats polling: latest sample at {result.Stats!.CapturedAtUtc.ToLocalTime():T}.";
                await Task.Delay(_service.Options.StatsPollInterval, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_closed)
            {
                _statsPolling = false;
                StatusText.Text = $"Recoverable stats polling error: {exception.Message}";
            }
        }
        finally
        {
            DisposeIfCurrent(ref _statsCancellation, source);
        }
    }

    private async Task RefreshLogsAsync()
    {
        if (!CanRead())
        {
            return;
        }

        _logsFollowing = false;
        CancelLogs();
        _logsCancellation = new CancellationTokenSource();
        var source = _logsCancellation;
        StatusText.Text = "Loading logs: reading recent timestamped Docker logs…";
        try
        {
            var result = await _service.ReadRecentLogsAsync(
                _profile,
                _container.Id,
                _service.Options.DefaultRecentLogRows,
                source.Token);
            if (!result.IsSuccess)
            {
                ApplyError("Logs", result.Error!);
                return;
            }

            _logBuffer.Reset(result.Entries);
            _lastLogTimestampToken = result.LastTimestampToken;
            ApplyLogFilter();
            StatusText.Text = result.Entries.Count == 0
                ? "Logs empty: the container returned no recent rows."
                : $"Logs loaded: {result.Entries.Count:N0} recent row(s).";
        }
        catch (OperationCanceledException)
        {
            if (!_closed)
            {
                StatusText.Text = "Cancelled: log read stopped.";
            }
        }
        finally
        {
            DisposeIfCurrent(ref _logsCancellation, source);
        }
    }

    private void StartLogFollowing()
    {
        if (!CanRead())
        {
            return;
        }

        CancelLogs();
        _logsFollowing = true;
        _logsCancellation = new CancellationTokenSource();
        var source = _logsCancellation;
        StatusText.Text = $"Following logs: polling every {_service.Options.LogPollInterval.TotalSeconds:0.##}s. Pause or Cancel at any time.";
        _ = FollowLogsAsync(source);
    }

    private async Task FollowLogsAsync(CancellationTokenSource source)
    {
        var token = source.Token;
        try
        {
            while (_logsFollowing && !_closed && !token.IsCancellationRequested)
            {
                DockerContainerLogReadResult result;
                if (string.IsNullOrWhiteSpace(_lastLogTimestampToken))
                {
                    result = await _service.ReadRecentLogsAsync(
                        _profile,
                        _container.Id,
                        _service.Options.DefaultRecentLogRows,
                        token);
                }
                else
                {
                    result = await _service.ReadLogsSinceAsync(
                        _profile,
                        _container.Id,
                        _lastLogTimestampToken,
                        _service.Options.DefaultRecentLogRows,
                        token);
                }

                if (!result.IsSuccess)
                {
                    if (result.Error?.Code == RemoteErrorCode.OperationCancelled && token.IsCancellationRequested)
                    {
                        break;
                    }

                    _logsFollowing = false;
                    ApplyError("Logs", result.Error!);
                    break;
                }

                _logBuffer.AddRange(result.Entries);
                if (!string.IsNullOrWhiteSpace(result.LastTimestampToken))
                {
                    _lastLogTimestampToken = result.LastTimestampToken;
                }

                ApplyLogFilter();
                StatusText.Text = $"Following logs: {_logBuffer.Entries.Count:N0} row(s) retained; newest poll returned {result.Entries.Count:N0}.";
                await Task.Delay(_service.Options.LogPollInterval, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_closed)
            {
                _logsFollowing = false;
                StatusText.Text = $"Recoverable log follow error: {exception.Message}";
            }
        }
        finally
        {
            DisposeIfCurrent(ref _logsCancellation, source);
        }
    }

    private void ApplyDetails(DockerContainerDetails details)
    {
        var stateParts = new List<string> { ValueOrDash(details.State.Status) };
        if (!string.IsNullOrWhiteSpace(details.State.HealthStatus))
        {
            stateParts.Add($"health {details.State.HealthStatus}");
        }

        if (details.State.OomKilled)
        {
            stateParts.Add("OOM killed");
        }

        DetailStateText.Text = string.Join(" · ", stateParts);
        DetailImageText.Text = ValueOrDash(details.Image);
        DetailPidText.Text = $"PID {details.State.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "—"} · exit {details.State.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "—"}";
        DetailWorkdirText.Text = ValueOrDash(details.WorkingDirectory);
        EnvironmentGrid.ItemsSource = details.Environment;
        MountGrid.ItemsSource = details.Mounts;
        DetailNetworkGrid.ItemsSource = details.Networks;
        LabelGrid.ItemsSource = details.Labels.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        var sensitive = details.Environment.Count(variable => variable.IsSensitive);
        FooterText.Text =
            $"Details: {details.Environment.Count:N0} env var(s), {sensitive:N0} redacted; {details.Mounts.Count:N0} mount(s); {details.Networks.Count:N0} network(s).";
        UpdateManagementButtons(details);
    }

    private void ApplyStats(DockerContainerStats stats)
    {
        CpuValue.Text = FormatPercent(stats.CpuPercent);
        MemoryValue.Text = $"{FormatBytes(stats.MemoryUsageBytes)} / {FormatBytes(stats.MemoryLimitBytes)}";
        MemoryPercentValue.Text = FormatPercent(stats.MemoryPercent);
        NetworkIoValue.Text = $"{FormatBytes(stats.NetworkInputBytes)} / {FormatBytes(stats.NetworkOutputBytes)}";
        BlockIoValue.Text = $"{FormatBytes(stats.BlockReadBytes)} / {FormatBytes(stats.BlockWriteBytes)}";
        PidsValue.Text = stats.ProcessCount?.ToString("N0", CultureInfo.CurrentCulture) ?? "—";
        StatsFooterText.Text = $"Captured {stats.CapturedAtUtc.ToLocalTime():F}. Docker stats is read-only and cancellable.";
    }

    private void ApplyLogFilter()
    {
        DockerLogStream? stream = null;
        if (LogStreamBox.SelectedItem is string selected &&
            !string.Equals(selected, "All", StringComparison.Ordinal) &&
            Enum.TryParse<DockerLogStream>(selected, out var parsed))
        {
            stream = parsed;
        }

        var visible = DockerContainerLogProjection.Filter(_logBuffer.Entries, LogSearchBox.Text, stream);
        LogGrid.ItemsSource = visible;
        LogFooterText.Text = $"{visible.Count:N0} visible / {_logBuffer.Entries.Count:N0} retained · max {_service.Options.MaxRetainedLogRows:N0}.";
    }

    private void ApplyError(string area, RemoteError error)
    {
        StatusText.Text = error.Code switch
        {
            RemoteErrorCode.PermissionDenied or RemoteErrorCode.SudoRequired => $"{area} permission denied: {error.Message}",
            RemoteErrorCode.PathNotFound => $"{area} not found: the container may have been removed. {error.Message}",
            RemoteErrorCode.PathConflict => $"{area} state conflict: {error.Message}",
            RemoteErrorCode.AmbiguousState => $"{area} ambiguous state: {error.Message}",
            RemoteErrorCode.CapabilityUnavailable or RemoteErrorCode.CommandNotFound => $"{area} capability unavailable: {error.Message}",
            RemoteErrorCode.UnsupportedVersion => $"{area} unsupported: {error.Message}",
            RemoteErrorCode.NetworkInterrupted or RemoteErrorCode.ConnectionFailed => $"{area} disconnected: {error.Message}",
            RemoteErrorCode.OperationCancelled => $"{area} cancelled: {error.Message}",
            RemoteErrorCode.ParseFailed => $"{area} malformed/partial output: {error.Message}",
            _ => $"{area} recoverable error ({error.Code}): {error.Message}",
        };
    }

    private bool CanRead()
    {
        if (_closed || _containerRemoved)
        {
            return false;
        }

        if (!_initiallyConnected)
        {
            StatusText.Text = "Disconnected: connect the server before reading or managing this container.";
            return false;
        }

        return true;
    }

    private void CancelAll()
    {
        _statsPolling = false;
        _logsFollowing = false;
        CancelDetails();
        CancelStats();
        CancelLogs();
    }

    private void CancelDetails()
    {
        if (_detailsCancellation is not null && !_detailsCancellation.IsCancellationRequested)
        {
            _detailsCancellation.Cancel();
        }
    }

    private void CancelStats()
    {
        if (_statsCancellation is not null && !_statsCancellation.IsCancellationRequested)
        {
            _statsCancellation.Cancel();
        }
    }

    private void CancelLogs()
    {
        if (_logsCancellation is not null && !_logsCancellation.IsCancellationRequested)
        {
            _logsCancellation.Cancel();
        }
    }

    private static void DisposeIfCurrent(
        ref CancellationTokenSource? current,
        CancellationTokenSource completed)
    {
        if (ReferenceEquals(current, completed))
        {
            current = null;
        }

        completed.Dispose();
    }

    private static string FormatPercent(double? value) => value is null ? "—" : $"{value:0.##}%";

    private static string FormatBytes(long? bytes)
    {
        if (bytes is null)
        {
            return "—";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes.Value;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.#} {units[index]}";
    }

    private static string ValueOrDash(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
}
