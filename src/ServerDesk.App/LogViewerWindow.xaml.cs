using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ServerDesk.Application.Logs;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class LogViewerWindow : Window
{
    private readonly IServerLogService _logService;
    private readonly ServerProfile _profile;
    private readonly bool _initiallyConnected;
    private readonly LogRetentionBuffer _retained;
    private CancellationTokenSource? _refreshCancellation;
    private CancellationTokenSource? _followCancellation;
    private Task _followTask = Task.CompletedTask;
    private string? _lastCursor;
    private bool _followRequested;
    private bool _paused;
    private bool _uiReady;
    private bool _closed;

    public LogViewerWindow(
        IServerLogService logService,
        ServerProfile profile,
        bool initiallyConnected,
        string? initialUnitFilter = null)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _initiallyConnected = initiallyConnected;
        _retained = new LogRetentionBuffer(_logService.Options.MaxRetainedRows);

        InitializeComponent();
        TitleText.Text = $"Logs · {_profile.Name}";
        EndpointText.Text = $"{_profile.Username}@{_profile.Host}:{_profile.Port}";
        SourceBox.ItemsSource = Enum.GetValues<ServerLogSource>();
        SourceBox.SelectedItem = ServerLogSource.Journal;
        SeverityBox.ItemsSource = new[] { "All" }.Concat(Enum.GetNames<LogSeverity>()).ToArray();
        SeverityBox.SelectedIndex = 0;
        if (!string.IsNullOrWhiteSpace(initialUnitFilter))
        {
            JournalUnitBox.Text = initialUnitFilter;
            UnitFilterBox.Text = initialUnitFilter;
        }

        StatusText.Text = initiallyConnected
            ? "Initial: choose a source or refresh recent logs."
            : "Disconnected: connect the server before loading logs.";
        FooterText.Text = $"Retention is bounded to {_logService.Options.MaxRetainedRows:N0} row(s).";
        _uiReady = true;
        UpdateSourceInputs();
    }

    private async void WindowOnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initiallyConnected)
        {
            await RefreshAsync();
        }
    }

    private void WindowOnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        CancelAll(keepFollowIntent: false);
    }

    private async void RefreshOnClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void FollowOnClick(object sender, RoutedEventArgs e)
    {
        if (!_initiallyConnected)
        {
            StatusText.Text = "Disconnected: connect the server before following logs.";
            return;
        }

        if (_retained.Entries.Count == 0)
        {
            await RefreshAsync();
            if (_retained.Entries.Count == 0 && CurrentSource == ServerLogSource.File && string.IsNullOrWhiteSpace(FilePathBox.Text))
            {
                return;
            }
        }

        StartFollowing();
    }

    private void PauseOnClick(object sender, RoutedEventArgs e)
    {
        if (!_followRequested || _paused)
        {
            StatusText.Text = "Pause is available while follow is active.";
            return;
        }

        _paused = true;
        CancelFollow(keepFollowIntent: true);
        StatusText.Text = $"Paused: {_retained.Entries.Count:N0} row(s) retained. Resume continues from the last journal cursor where available.";
    }

    private void ResumeOnClick(object sender, RoutedEventArgs e)
    {
        if (!_followRequested || !_paused)
        {
            StatusText.Text = "Resume is available after pausing follow.";
            return;
        }

        _paused = false;
        StartFollowing();
    }

    private void CancelOnClick(object sender, RoutedEventArgs e)
    {
        CancelAll(keepFollowIntent: false);
        StatusText.Text = "Cancelled: active log work stopped.";
    }

    private async void ExportOnClick(object sender, RoutedEventArgs e)
    {
        var visible = (LogGrid.ItemsSource as IEnumerable<LogEntry>)?.ToArray() ?? [];
        if (visible.Length == 0)
        {
            StatusText.Text = "Export: there are no visible rows to save.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export visible ServerDesk logs",
            Filter = "Tab-separated log (*.tsv)|*.tsv|Text file (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = ".tsv",
            AddExtension = true,
            FileName = $"serverdesk-{_profile.Name}-logs.tsv",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var content = BuildExport(visible);
            await File.WriteAllTextAsync(dialog.FileName, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            StatusText.Text = $"Exported {visible.Length:N0} visible row(s) as UTF-8 text.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            StatusText.Text = $"Local I/O error: ServerDesk could not export logs. {exception.Message}";
        }
    }

    private void SourceBoxOnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        CancelAll(keepFollowIntent: false);
        _retained.Clear();
        _lastCursor = null;
        UpdateSourceInputs();
        ApplyFilter();
        StatusText.Text = CurrentSource == ServerLogSource.Journal
            ? "Initial: journald selected. Refresh or Follow to load structured entries."
            : "Initial: file source selected. Enter an absolute remote log path, then Refresh or Follow.";
    }

    private void FilterOnChanged(object sender, TextChangedEventArgs e)
    {
        if (_uiReady)
        {
            ApplyFilter();
        }
    }

    private void FilterOnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_uiReady)
        {
            ApplyFilter();
        }
    }

    private async Task RefreshAsync()
    {
        if (!_initiallyConnected)
        {
            StatusText.Text = "Disconnected: connect the server before loading logs.";
            return;
        }

        CancelFollow(keepFollowIntent: false);
        CancelRefresh();
        _refreshCancellation = new CancellationTokenSource();
        var source = _refreshCancellation;
        StatusText.Text = "Loading: reading recent remote logs…";
        try
        {
            var result = await ReadCurrentAsync(incremental: false, source.Token);
            if (!result.IsSuccess)
            {
                ApplyError(result.Error!);
                return;
            }

            _retained.Reset(result.Entries);
            _lastCursor = result.LastCursor;
            ApplyFilter();
            StatusText.Text = result.Entries.Count == 0
                ? "Empty: the selected log source returned no rows."
                : $"Loaded: {result.Entries.Count:N0} row(s) read; {_retained.Entries.Count:N0} retained.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled: log refresh stopped.";
        }
        catch (ArgumentException exception)
        {
            StatusText.Text = $"Recoverable input error: {exception.Message}";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Recoverable error: {exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, source))
            {
                _refreshCancellation = null;
            }

            source.Dispose();
        }
    }

    private void StartFollowing()
    {
        if (_closed)
        {
            return;
        }

        CancelRefresh();
        CancelFollow(keepFollowIntent: true);
        _followRequested = true;
        _paused = false;
        _followCancellation = new CancellationTokenSource();
        var source = _followCancellation;
        StatusText.Text = $"Following: polling every {_logService.Options.FollowPollInterval.TotalSeconds:0.##}s. Pause or Cancel at any time.";
        _followTask = FollowLoopAsync(source);
    }

    private async Task FollowLoopAsync(CancellationTokenSource source)
    {
        var token = source.Token;
        try
        {
            while (!token.IsCancellationRequested && !_closed && _followRequested && !_paused)
            {
                ServerLogReadResult result;
                try
                {
                    result = await ReadCurrentAsync(
                        incremental: CurrentSource == ServerLogSource.Journal && !string.IsNullOrWhiteSpace(_lastCursor),
                        token);
                }
                catch (ArgumentException exception)
                {
                    StatusText.Text = $"Recoverable input error: {exception.Message}";
                    _followRequested = false;
                    break;
                }

                if (!result.IsSuccess)
                {
                    if (result.Error?.Code == RemoteErrorCode.OperationCancelled && token.IsCancellationRequested)
                    {
                        break;
                    }

                    ApplyError(result.Error!);
                    _followRequested = false;
                    break;
                }

                if (CurrentSource == ServerLogSource.Journal)
                {
                    _retained.AddRange(result.Entries);
                    if (!string.IsNullOrWhiteSpace(result.LastCursor))
                    {
                        _lastCursor = result.LastCursor;
                    }
                }
                else
                {
                    _retained.Reset(result.Entries);
                }

                ApplyFilter();
                StatusText.Text = $"Following: {_retained.Entries.Count:N0} row(s) retained; newest poll returned {result.Entries.Count:N0}.";
                await Task.Delay(_logService.Options.FollowPollInterval, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_closed)
            {
                StatusText.Text = $"Recoverable follow error: {exception.Message}";
                _followRequested = false;
            }
        }
        finally
        {
            if (ReferenceEquals(_followCancellation, source))
            {
                _followCancellation = null;
            }

            source.Dispose();
        }
    }

    private Task<ServerLogReadResult> ReadCurrentAsync(bool incremental, CancellationToken cancellationToken)
    {
        if (CurrentSource == ServerLogSource.Journal)
        {
            var unit = string.IsNullOrWhiteSpace(JournalUnitBox.Text) ? null : JournalUnitBox.Text.Trim();
            return incremental && !string.IsNullOrWhiteSpace(_lastCursor)
                ? _logService.ReadJournalAfterCursorAsync(
                    _profile,
                    _lastCursor,
                    _logService.Options.DefaultRecentRows,
                    unit,
                    cancellationToken)
                : _logService.ReadJournalAsync(
                    _profile,
                    _logService.Options.DefaultRecentRows,
                    unit,
                    cancellationToken);
        }

        var path = FilePathBox.Text.Trim();
        if (path.Length == 0)
        {
            throw new ArgumentException("Enter an absolute remote text log path, for example /var/log/nginx/error.log.");
        }

        return _logService.ReadFileTailAsync(
            _profile,
            path,
            _logService.Options.DefaultRecentRows,
            cancellationToken);
    }

    private void ApplyFilter()
    {
        if (!_uiReady)
        {
            return;
        }

        LogSeverity? severity = null;
        if (SeverityBox.SelectedItem is string selectedSeverity &&
            !string.Equals(selectedSeverity, "All", StringComparison.Ordinal) &&
            Enum.TryParse<LogSeverity>(selectedSeverity, out var parsedSeverity))
        {
            severity = parsedSeverity;
        }

        var filter = new ServerLogFilter(
            SearchBox.Text,
            severity,
            IdentifierFilterBox.Text,
            UnitFilterBox.Text,
            CurrentSource);
        var visible = ServerLogProjection.Filter(_retained.Entries, filter);
        LogGrid.ItemsSource = visible;
        FooterText.Text = $"{visible.Count:N0} visible / {_retained.Entries.Count:N0} retained · max {_logService.Options.MaxRetainedRows:N0}. Filters are client-side.";
    }

    private void ApplyError(RemoteError error)
    {
        StatusText.Text = error.Code switch
        {
            RemoteErrorCode.PermissionDenied or RemoteErrorCode.SudoRequired => $"Permission denied: {error.Message}",
            RemoteErrorCode.CommandNotFound or RemoteErrorCode.CapabilityUnavailable or RemoteErrorCode.UnsupportedVersion => $"Capability unavailable: {error.Message}",
            RemoteErrorCode.PathNotFound => $"Not found: {error.Message}",
            RemoteErrorCode.NetworkInterrupted or RemoteErrorCode.ConnectionFailed => $"Disconnected: {error.Message}",
            RemoteErrorCode.OperationCancelled => $"Cancelled: {error.Message}",
            RemoteErrorCode.ParseFailed => $"Malformed/partial source: {error.Message}",
            _ => $"Recoverable error ({error.Code}): {error.Message}",
        };
    }

    private void UpdateSourceInputs()
    {
        var journal = CurrentSource == ServerLogSource.Journal;
        JournalUnitBox.IsEnabled = journal;
        FilePathBox.IsEnabled = !journal;
    }

    private ServerLogSource CurrentSource =>
        SourceBox.SelectedItem is ServerLogSource source ? source : ServerLogSource.Journal;

    private void CancelAll(bool keepFollowIntent)
    {
        CancelRefresh();
        CancelFollow(keepFollowIntent);
    }

    private void CancelRefresh()
    {
        if (_refreshCancellation is not null && !_refreshCancellation.IsCancellationRequested)
        {
            _refreshCancellation.Cancel();
        }
    }

    private void CancelFollow(bool keepFollowIntent)
    {
        if (!keepFollowIntent)
        {
            _followRequested = false;
            _paused = false;
        }

        if (_followCancellation is not null && !_followCancellation.IsCancellationRequested)
        {
            _followCancellation.Cancel();
        }
    }

    private static string BuildExport(IEnumerable<LogEntry> entries)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Timestamp\tSeverity\tSource\tUnit\tIdentifier\tPID\tHostname\tMessage");
        foreach (var entry in entries)
        {
            builder.Append(EscapeTsv(entry.Timestamp?.ToString("O") ?? string.Empty)).Append('\t')
                .Append(entry.Severity).Append('\t')
                .Append(entry.Source).Append('\t')
                .Append(EscapeTsv(entry.SystemdUnit)).Append('\t')
                .Append(EscapeTsv(entry.Identifier)).Append('\t')
                .Append(entry.ProcessId?.ToString() ?? string.Empty).Append('\t')
                .Append(EscapeTsv(entry.Hostname)).Append('\t')
                .Append(EscapeTsv(entry.Message)).AppendLine();
        }

        return builder.ToString();
    }

    private static string EscapeTsv(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);
}
