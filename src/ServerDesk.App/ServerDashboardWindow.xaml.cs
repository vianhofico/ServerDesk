using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using ServerDesk.App.Presentation;
using ServerDesk.Application.Dashboard;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class ServerDashboardWindow : Window
{
    private readonly IServerDashboardService _dashboardService;
    private readonly ServerProfile _profile;
    private readonly DashboardWindowViewModel _viewModel;
    private readonly bool _connectedAtOpen;
    private CancellationTokenSource? _refreshCancellation;

    public ServerDashboardWindow(
        IServerDashboardService dashboardService,
        ServerProfile profile,
        bool isConnected)
    {
        InitializeComponent();
        _dashboardService = dashboardService ?? throw new ArgumentNullException(nameof(dashboardService));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _connectedAtOpen = isConnected;
        _viewModel = new DashboardWindowViewModel(profile);
        DataContext = _viewModel;
        Loaded += DashboardWindowOnLoaded;
        Closed += DashboardWindowOnClosed;

        if (!isConnected)
        {
            _viewModel.StatusMessage = "Connect this server before refreshing dashboard metrics.";
        }

        UpdateButtons();
    }

    private async void DashboardWindowOnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= DashboardWindowOnLoaded;
        if (_connectedAtOpen)
        {
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    private void DashboardWindowOnClosed(object? sender, EventArgs e)
    {
        Closed -= DashboardWindowOnClosed;
        CancelRefresh();
    }

    private async void RefreshOnClick(object sender, RoutedEventArgs e)
    {
        if (!_connectedAtOpen)
        {
            _viewModel.StatusMessage = "This dashboard was opened while the server was disconnected. Close it, connect, then reopen the dashboard.";
            return;
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    private void CancelOnClick(object sender, RoutedEventArgs e)
    {
        if (_refreshCancellation is not null)
        {
            _refreshCancellation.Cancel();
        }
    }

    private async Task RefreshAsync()
    {
        CancelRefresh();
        var cancellation = new CancellationTokenSource();
        _refreshCancellation = cancellation;
        _viewModel.IsBusy = true;
        _viewModel.StatusMessage = "Sampling CPU and network counters, then reading memory, load, uptime and filesystem usage…";
        UpdateButtons();

        try
        {
            var snapshot = await _dashboardService.GetAsync(_profile, cancellation.Token).ConfigureAwait(true);
            if (!cancellation.IsCancellationRequested && ReferenceEquals(_refreshCancellation, cancellation))
            {
                _viewModel.Apply(snapshot);
                _viewModel.StatusMessage = snapshot.Warnings.Count == 0
                    ? "Dashboard refreshed successfully."
                    : $"Dashboard refreshed with {snapshot.Warnings.Count} health warning(s).";
            }
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_refreshCancellation, cancellation))
            {
                _viewModel.StatusMessage = "Dashboard refresh was cancelled.";
            }
        }
        catch (ServerDashboardException exception)
        {
            if (ReferenceEquals(_refreshCancellation, cancellation))
            {
                _viewModel.StatusMessage = $"Dashboard refresh failed: {exception.Error.Message}";
            }
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_refreshCancellation, cancellation))
            {
                _viewModel.StatusMessage = $"Dashboard refresh failed: {exception.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, cancellation))
            {
                _refreshCancellation = null;
                _viewModel.IsBusy = false;
                UpdateButtons();
            }

            cancellation.Dispose();
        }
    }

    private void CancelRefresh()
    {
        if (_refreshCancellation is null)
        {
            return;
        }

        _refreshCancellation.Cancel();
        _refreshCancellation = null;
    }

    private void UpdateButtons()
    {
        RefreshButton.IsEnabled = _connectedAtOpen && !_viewModel.IsBusy;
        CancelButton.IsEnabled = _viewModel.IsBusy;
    }
}

internal sealed class DashboardWindowViewModel : ObservableObject
{
    private bool _isBusy;
    private string _capturedText = "No snapshot captured yet.";
    private string? _statusMessage;

    public DashboardWindowViewModel(ServerProfile profile)
    {
        Title = $"Dashboard · {profile.Name}";
        Endpoint = $"{profile.Username}@{profile.Host}:{profile.Port}";
        MetricCards = [];
        Warnings = [];
        FileSystems = [];
        ResetCards();
    }

    public string Title { get; }

    public string Endpoint { get; }

    public ObservableCollection<DashboardMetricCardRow> MetricCards { get; }

    public ObservableCollection<DashboardWarningRow> Warnings { get; }

    public ObservableCollection<DashboardFileSystemRow> FileSystems { get; }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public string CapturedText
    {
        get => _capturedText;
        private set => SetProperty(ref _capturedText, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatusMessage));
            }
        }
    }

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public bool HasNoWarnings => Warnings.Count == 0;

    public string WarningCountText => Warnings.Count == 1 ? "1 warning" : $"{Warnings.Count} warnings";

    public void Apply(ServerDashboardSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        MetricCards.Clear();

        AddCpu(snapshot.Cpu);
        AddLoad(snapshot.Load);
        AddUptime(snapshot.Uptime);
        AddMemory(snapshot.Memory);
        AddSwap(snapshot.Memory);
        AddNetwork(snapshot.Network, received: true);
        AddNetwork(snapshot.Network, received: false);

        Warnings.Clear();
        foreach (var warning in snapshot.Warnings)
        {
            Warnings.Add(new DashboardWarningRow(warning.Severity.ToString(), warning.Message));
        }

        FileSystems.Clear();
        if (snapshot.FileSystems.Value is not null)
        {
            foreach (var row in snapshot.FileSystems.Value.OrderBy(row => row.MountPoint, StringComparer.Ordinal))
            {
                FileSystems.Add(new DashboardFileSystemRow(
                    row.MountPoint,
                    row.FileSystemType,
                    FormatBytes(row.UsedBytes),
                    FormatBytes(row.AvailableBytes),
                    $"{row.UsedPercent:F0}%"));
            }
        }

        CapturedText = $"Captured {snapshot.CapturedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)} · read-only SSH snapshot";
        OnPropertyChanged(nameof(HasNoWarnings));
        OnPropertyChanged(nameof(WarningCountText));
    }

    private void ResetCards()
    {
        MetricCards.Clear();
        foreach (var label in new[] { "CPU", "Load average", "Uptime", "Memory", "Swap", "Network RX", "Network TX" })
        {
            MetricCards.Add(new DashboardMetricCardRow(label, "—", "Connect and refresh to read this metric."));
        }
    }

    private void AddCpu(DashboardSection<CpuMetrics> section)
    {
        MetricCards.Add(section.Value is { } value
            ? new DashboardMetricCardRow("CPU", $"{value.UtilizationPercent:F1}%", $"{value.LogicalProcessors} logical processor(s)")
            : MissingCard("CPU", section.Status, section.Detail));
    }

    private void AddLoad(DashboardSection<LoadMetrics> section)
    {
        MetricCards.Add(section.Value is { } value
            ? new DashboardMetricCardRow(
                "Load average",
                $"{value.OneMinute:F2} · {value.FiveMinutes:F2} · {value.FifteenMinutes:F2}",
                "1 / 5 / 15 minute load")
            : MissingCard("Load average", section.Status, section.Detail));
    }

    private void AddUptime(DashboardSection<UptimeMetrics> section)
    {
        MetricCards.Add(section.Value is { } value
            ? new DashboardMetricCardRow("Uptime", FormatDuration(value.Uptime), "Time since the last boot")
            : MissingCard("Uptime", section.Status, section.Detail));
    }

    private void AddMemory(DashboardSection<MemoryMetrics> section)
    {
        MetricCards.Add(section.Value is { } value
            ? new DashboardMetricCardRow(
                "Memory",
                $"{value.UsedPercent:F1}%",
                $"{FormatBytes(value.UsedBytes)} used · {FormatBytes(value.AvailableBytes)} available · {FormatBytes(value.TotalBytes)} total")
            : MissingCard("Memory", section.Status, section.Detail));
    }

    private void AddSwap(DashboardSection<MemoryMetrics> section)
    {
        if (section.Value is not { } value)
        {
            MetricCards.Add(MissingCard("Swap", section.Status, section.Detail));
            return;
        }

        MetricCards.Add(value.SwapTotalBytes == 0
            ? new DashboardMetricCardRow("Swap", "Disabled", "No configured swap was reported.")
            : new DashboardMetricCardRow(
                "Swap",
                $"{value.SwapUsedPercent:F1}%",
                $"{FormatBytes(value.SwapUsedBytes)} used · {FormatBytes(value.SwapTotalBytes)} total"));
    }

    private void AddNetwork(DashboardSection<NetworkMetrics> section, bool received)
    {
        var label = received ? "Network RX" : "Network TX";
        if (section.Value is not { } value)
        {
            MetricCards.Add(MissingCard(label, section.Status, section.Detail));
            return;
        }

        var rate = received ? value.ReceivedBytesPerSecond : value.TransmittedBytesPerSecond;
        var total = received ? value.ReceivedBytes : value.TransmittedBytes;
        MetricCards.Add(new DashboardMetricCardRow(
            label,
            $"{FormatBytesPerSecond(rate)}/s",
            $"{FormatBytes(total)} cumulative · {value.Interfaces.Count} interface(s)"));
    }

    private static DashboardMetricCardRow MissingCard(
        string label,
        DashboardSectionStatus status,
        string? detail) =>
        new(label, "—", $"{status}: {detail ?? "No data."}");

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalDays >= 1)
        {
            return $"{(int)value.TotalDays}d {value.Hours}h {value.Minutes}m";
        }

        if (value.TotalHours >= 1)
        {
            return $"{(int)value.TotalHours}h {value.Minutes}m";
        }

        return $"{Math.Max(0, (int)value.TotalMinutes)}m";
    }

    private static string FormatBytes(long value) => FormatBytes((double)value);

    private static string FormatBytesPerSecond(double value) => FormatBytes(value);

    private static string FormatBytes(double value)
    {
        var units = new[] { "B", "KB", "MB", "GB", "TB", "PB" };
        var index = 0;
        value = Math.Max(0d, value);
        while (value >= 1024d && index < units.Length - 1)
        {
            value /= 1024d;
            index++;
        }

        return index == 0
            ? $"{value:F0} {units[index]}"
            : $"{value:F1} {units[index]}";
    }
}

internal sealed record DashboardMetricCardRow(string Label, string Value, string Detail);

internal sealed record DashboardWarningRow(string Severity, string Message);

internal sealed record DashboardFileSystemRow(
    string MountPoint,
    string FileSystemType,
    string Used,
    string Available,
    string UsedPercent);
