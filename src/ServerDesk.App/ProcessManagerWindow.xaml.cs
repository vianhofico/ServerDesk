using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ServerDesk.Application.Processes;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class ProcessManagerWindow : Window
{
    private readonly IServerProcessService _processService;
    private readonly ServerProfile _profile;
    private readonly bool _initiallyConnected;
    private readonly List<ProcessRow> _allRows = [];
    private CancellationTokenSource? _operationCancellation;

    public ProcessManagerWindow(
        IServerProcessService processService,
        ServerProfile profile,
        bool initiallyConnected)
    {
        _processService = processService ?? throw new ArgumentNullException(nameof(processService));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _initiallyConnected = initiallyConnected;
        InitializeComponent();
        TitleText.Text = $"Processes · {_profile.Name}";
        EndpointText.Text = $"{_profile.Username}@{_profile.Host}:{_profile.Port}";
        StatusText.Text = initiallyConnected
            ? "Ready to load processes."
            : "Disconnected: connect the server before loading processes.";
        DetailsText.Text = "Select a process to inspect its normalized details.";
        FooterText.Text = "No process data loaded.";
    }

    private async void WindowOnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initiallyConnected)
        {
            await RefreshAsync();
        }
    }

    private void WindowOnClosed(object? sender, EventArgs e) => CancelActiveOperation();

    private async void RefreshOnClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void CancelOnClick(object sender, RoutedEventArgs e) => CancelActiveOperation();

    private async void TerminateOnClick(object sender, RoutedEventArgs e) =>
        await SignalSelectedAsync(ServerProcessSignal.Terminate);

    private async void ForceKillOnClick(object sender, RoutedEventArgs e) =>
        await SignalSelectedAsync(ServerProcessSignal.ForceKill);

    private void SearchBoxOnTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ProcessGridOnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProcessGrid.SelectedItem is not ProcessRow row)
        {
            DetailsText.Text = "Select a process to inspect its normalized details.";
            return;
        }

        DetailsText.Text =
            $"PID: {row.ProcessId}\n" +
            $"Parent PID: {row.ParentProcessId}\n" +
            $"User: {row.User}\n" +
            $"State: {row.State}\n" +
            $"CPU: {row.CpuText}\n" +
            $"Resident memory: {row.MemoryText}\n" +
            $"Elapsed: {row.ElapsedText}\n" +
            $"Command: {row.Command}\n\n" +
            row.Arguments;
    }

    private async Task RefreshAsync()
    {
        using var operation = BeginOperation();
        StatusText.Text = "Loading normalized process list…";
        try
        {
            var result = await _processService.ListAsync(_profile, operation.Token);
            if (!result.IsSuccess)
            {
                ApplyError(result.Error!);
                return;
            }

            _allRows.Clear();
            _allRows.AddRange(result.Processes.Select(ProcessRow.From));
            ApplyFilter();
            StatusText.Text = _allRows.Count == 0
                ? "Empty: the server returned no visible processes."
                : $"Ready: {_allRows.Count:N0} process(es) loaded.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled: process refresh stopped.";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Error: {exception.Message}";
        }
    }

    private async Task SignalSelectedAsync(ServerProcessSignal signal)
    {
        if (ProcessGrid.SelectedItem is not ProcessRow row)
        {
            StatusText.Text = "Select a process first.";
            return;
        }

        var force = signal == ServerProcessSignal.ForceKill;
        var prompt = force
            ? $"Force kill PID {row.ProcessId} ({row.Command}) with SIGKILL?\n\nThe process cannot clean up or save state. This is destructive and may disrupt workloads."
            : $"Terminate PID {row.ProcessId} ({row.Command}) with SIGTERM?\n\nThe process may shut down gracefully, but the operation can still disrupt workloads.";
        if (MessageBox.Show(
                this,
                prompt,
                force ? "Confirm force kill" : "Confirm terminate",
                MessageBoxButton.OKCancel,
                force ? MessageBoxImage.Error : MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        if (force && MessageBox.Show(
                this,
                $"Final confirmation: send SIGKILL to PID {row.ProcessId} ({row.Command}) now?\n\nThis cannot be undone. ServerDesk will not automatically retry if the connection drops after the signal is sent.",
                "Final force-kill confirmation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Error,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        using var operation = BeginOperation();
        StatusText.Text = force
            ? $"Sending SIGKILL to PID {row.ProcessId}…"
            : $"Sending SIGTERM to PID {row.ProcessId}…";
        try
        {
            var result = await _processService.SignalAsync(
                _profile,
                row.ProcessId,
                signal,
                operation.Token);
            if (!result.IsSuccess)
            {
                ApplyError(result.Error!);
                return;
            }

            StatusText.Text = $"{result.Message} Refreshing to verify current state…";
            await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled or interrupted: refresh the process list before deciding whether to retry a signal.";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Error: {exception.Message}";
        }
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        var rows = query.Length == 0
            ? _allRows
            : _allRows.Where(row => row.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        ProcessGrid.ItemsSource = rows;
        FooterText.Text = query.Length == 0
            ? $"{rows.Count:N0} process(es)."
            : $"{rows.Count:N0} of {_allRows.Count:N0} process(es) match '{query}'.";
    }

    private void ApplyError(RemoteError error)
    {
        StatusText.Text = error.Code switch
        {
            RemoteErrorCode.PermissionDenied => $"Permission: {error.Message}",
            RemoteErrorCode.CommandNotFound or RemoteErrorCode.CapabilityUnavailable => $"Capability: {error.Message}",
            RemoteErrorCode.NetworkInterrupted => $"Disconnected: {error.Message}",
            RemoteErrorCode.OperationCancelled => $"Cancelled: {error.Message}",
            RemoteErrorCode.AmbiguousState => $"Ambiguous state: {error.Message}",
            _ => $"{error.Code}: {error.Message}",
        };
    }

    private OperationScope BeginOperation()
    {
        CancelActiveOperation();
        _operationCancellation = new CancellationTokenSource();
        return new OperationScope(this, _operationCancellation);
    }

    private void CancelActiveOperation()
    {
        if (_operationCancellation is not null && !_operationCancellation.IsCancellationRequested)
        {
            _operationCancellation.Cancel();
        }
    }

    private sealed class OperationScope : IDisposable
    {
        private readonly ProcessManagerWindow _owner;
        private readonly CancellationTokenSource _source;
        private bool _disposed;

        public OperationScope(ProcessManagerWindow owner, CancellationTokenSource source)
        {
            _owner = owner;
            _source = source;
        }

        public CancellationToken Token => _source.Token;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (ReferenceEquals(_owner._operationCancellation, _source))
            {
                _owner._operationCancellation = null;
            }

            _source.Dispose();
        }
    }

    private sealed record ProcessRow(
        int ProcessId,
        int ParentProcessId,
        string User,
        string State,
        string CpuText,
        string MemoryText,
        string ElapsedText,
        string Command,
        string Arguments,
        string SearchText)
    {
        public static ProcessRow From(ServerProcessInfo process)
        {
            var memory = FormatBytes(process.ResidentBytes);
            var elapsed = FormatElapsed(process.Elapsed);
            return new ProcessRow(
                process.ProcessId,
                process.ParentProcessId,
                process.User,
                process.State,
                process.CpuPercent.ToString("0.0", CultureInfo.InvariantCulture) + "%",
                memory,
                elapsed,
                process.Command,
                process.Arguments,
                $"{process.ProcessId} {process.ParentProcessId} {process.User} {process.State} {process.Command} {process.Arguments}");
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
            var value = (double)Math.Max(0, bytes);
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return $"{value:0.#} {units[unit]}";
        }

        private static string FormatElapsed(TimeSpan elapsed) =>
            elapsed.TotalDays >= 1
                ? $"{(int)elapsed.TotalDays}d {elapsed:hh\\:mm\\:ss}"
                : elapsed.ToString("hh\\:mm\\:ss", CultureInfo.InvariantCulture);
    }
}
