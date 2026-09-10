using System.Globalization;
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
        ServerNameText.Text = _profile.Name;
        EndpointText.Text = $"{_profile.Username}@{_profile.Host}:{_profile.Port}";
        if (string.IsNullOrWhiteSpace(_profile.Environment))
        {
            EnvironmentValueText.SetResourceReference(TextBlock.TextProperty, "Loc.Logs.Header.Unlabeled");
        }
        else
        {
            EnvironmentValueText.Text = _profile.Environment;
        }

        ConnectionValueText.SetResourceReference(
            TextBlock.TextProperty,
            initiallyConnected ? "Loc.Logs.Connection.Connected" : "Loc.Logs.Connection.Disconnected");

        SourceBox.ItemsSource = Enum.GetValues<ServerLogSource>();
        SourceBox.SelectedItem = ServerLogSource.Journal;
        SeverityBox.ItemsSource = new[] { Localize("Loc.Logs.Filter.All") }
            .Concat(Enum.GetNames<LogSeverity>())
            .ToArray();
        SeverityBox.SelectedIndex = 0;
        if (!string.IsNullOrWhiteSpace(initialUnitFilter))
        {
            JournalUnitBox.Text = initialUnitFilter;
            UnitFilterBox.Text = initialUnitFilter;
        }

        SetStatusResource(
            initiallyConnected ? LogUiState.Initial : LogUiState.Disconnected,
            initiallyConnected ? "Loc.Logs.Status.ReadyInitial" : "Loc.Logs.Status.DisconnectedInitial");
        FooterText.Text = FormatLocalize("Loc.Logs.Footer.RetentionFormat", _logService.Options.MaxRetainedRows);
        RetentionLimitText.Text = _logService.Options.MaxRetainedRows.ToString("N0", CultureInfo.CurrentCulture);
        _uiReady = true;
        UpdateSourceInputs();
        ApplyFilter();
        UpdateCommandState();
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
            SetStatusResource(LogUiState.Disconnected, "Loc.Logs.Status.DisconnectedFollow");
            return;
        }

        if (_retained.Entries.Count == 0)
        {
            await RefreshAsync();
            if (_retained.Entries.Count == 0 &&
                CurrentSource == ServerLogSource.File &&
                string.IsNullOrWhiteSpace(FilePathBox.Text))
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
            SetStatusResource(LogUiState.Ready, "Loc.Logs.Status.PauseUnavailable");
            return;
        }

        _paused = true;
        CancelFollow(keepFollowIntent: true);
        SetStatusRaw(
            LogUiState.Paused,
            FormatLocalize("Loc.Logs.Status.PausedFormat", _retained.Entries.Count));
        UpdateCommandState();
    }

    private void ResumeOnClick(object sender, RoutedEventArgs e)
    {
        if (!_followRequested || !_paused)
        {
            SetStatusResource(LogUiState.Ready, "Loc.Logs.Status.ResumeUnavailable");
            return;
        }

        _paused = false;
        StartFollowing();
    }

    private void CancelOnClick(object sender, RoutedEventArgs e)
    {
        CancelAll(keepFollowIntent: false);
        SetStatusResource(LogUiState.Cancelled, "Loc.Logs.Status.Cancelled");
        UpdateCommandState();
    }

    private async void ExportOnClick(object sender, RoutedEventArgs e)
    {
        var visible = (LogGrid.ItemsSource as IEnumerable<LogEntry>)?.ToArray() ?? [];
        if (visible.Length == 0)
        {
            SetStatusResource(LogUiState.Empty, "Loc.Logs.Status.ExportEmpty");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = Localize("Loc.Logs.Export.DialogTitle"),
            Filter = Localize("Loc.Logs.Export.Filter"),
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
            SetStatusRaw(
                LogUiState.Ready,
                FormatLocalize("Loc.Logs.Status.ExportedFormat", visible.Length));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            SetStatusRaw(
                LogUiState.Error,
                FormatLocalize("Loc.Logs.Status.ExportErrorFormat", exception.Message));
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
        SetStatusResource(
            LogUiState.Initial,
            CurrentSource == ServerLogSource.Journal
                ? "Loc.Logs.Status.JournalSelected"
                : "Loc.Logs.Status.FileSelected");
        UpdateCommandState();
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

    private void ClearSearchOnClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private async Task RefreshAsync()
    {
        if (!_initiallyConnected)
        {
            SetStatusResource(LogUiState.Disconnected, "Loc.Logs.Status.DisconnectedInitial");
            return;
        }

        CancelFollow(keepFollowIntent: false);
        CancelRefresh();
        _refreshCancellation = new CancellationTokenSource();
        var source = _refreshCancellation;
        SetStatusResource(LogUiState.Loading, "Loc.Logs.Status.Loading");
        UpdateCommandState();
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
            if (result.Entries.Count == 0)
            {
                SetStatusResource(LogUiState.Empty, "Loc.Logs.Status.Empty");
            }
            else
            {
                SetStatusRaw(
                    LogUiState.Ready,
                    FormatLocalize("Loc.Logs.Status.LoadedFormat", result.Entries.Count, _retained.Entries.Count));
            }
        }
        catch (OperationCanceledException)
        {
            SetStatusResource(LogUiState.Cancelled, "Loc.Logs.Status.RefreshCancelled");
        }
        catch (ArgumentException exception)
        {
            SetStatusRaw(
                LogUiState.Error,
                FormatLocalize("Loc.Logs.Status.InputErrorFormat", exception.Message));
        }
        catch (Exception exception)
        {
            SetStatusRaw(
                LogUiState.Error,
                FormatLocalize("Loc.Logs.Status.ErrorFormat", exception.Message));
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, source))
            {
                _refreshCancellation = null;
            }

            source.Dispose();
            UpdateCommandState();
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
        SetStatusRaw(
            LogUiState.Following,
            FormatLocalize(
                "Loc.Logs.Status.FollowingStartFormat",
                _logService.Options.FollowPollInterval.TotalSeconds));
        UpdateCommandState();
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
                    SetStatusRaw(
                        LogUiState.Error,
                        FormatLocalize("Loc.Logs.Status.InputErrorFormat", exception.Message));
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
                SetStatusRaw(
                    LogUiState.Following,
                    FormatLocalize(
                        "Loc.Logs.Status.FollowingUpdateFormat",
                        _retained.Entries.Count,
                        result.Entries.Count));
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
                SetStatusRaw(
                    LogUiState.Error,
                    FormatLocalize("Loc.Logs.Status.FollowErrorFormat", exception.Message));
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
            UpdateCommandState();
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
            throw new ArgumentException(Localize("Loc.Logs.Status.FilePathRequired"));
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
        if (SeverityBox.SelectedIndex > 0 &&
            SeverityBox.SelectedItem is string selectedSeverity &&
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
        ClearSearchButton.Visibility = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
        VisibleCountText.Text = visible.Count.ToString("N0", CultureInfo.CurrentCulture);
        RetainedCountText.Text = _retained.Entries.Count.ToString("N0", CultureInfo.CurrentCulture);
        RetentionLimitText.Text = _logService.Options.MaxRetainedRows.ToString("N0", CultureInfo.CurrentCulture);
        FooterText.Text = FormatLocalize(
            "Loc.Logs.Footer.VisibleFormat",
            visible.Count,
            _retained.Entries.Count,
            _logService.Options.MaxRetainedRows);
        UpdateCommandState();
    }

    private void ApplyError(RemoteError error)
    {
        switch (error.Code)
        {
            case RemoteErrorCode.PermissionDenied:
            case RemoteErrorCode.SudoRequired:
                SetStatusRaw(
                    LogUiState.Permission,
                    FormatLocalize("Loc.Logs.Status.PermissionFormat", error.Message));
                break;

            case RemoteErrorCode.CommandNotFound:
            case RemoteErrorCode.CapabilityUnavailable:
            case RemoteErrorCode.UnsupportedVersion:
                SetStatusRaw(
                    LogUiState.Capability,
                    FormatLocalize("Loc.Logs.Status.CapabilityFormat", error.Message));
                break;

            case RemoteErrorCode.PathNotFound:
                SetStatusRaw(
                    LogUiState.NotFound,
                    FormatLocalize("Loc.Logs.Status.NotFoundFormat", error.Message));
                break;

            case RemoteErrorCode.NetworkInterrupted:
            case RemoteErrorCode.ConnectionFailed:
                SetStatusRaw(
                    LogUiState.Disconnected,
                    FormatLocalize("Loc.Logs.Status.DisconnectedErrorFormat", error.Message));
                break;

            case RemoteErrorCode.OperationCancelled:
                SetStatusRaw(
                    LogUiState.Cancelled,
                    FormatLocalize("Loc.Logs.Status.CancelledErrorFormat", error.Message));
                break;

            case RemoteErrorCode.ParseFailed:
                SetStatusRaw(
                    LogUiState.Error,
                    FormatLocalize("Loc.Logs.Status.MalformedFormat", error.Message));
                break;

            default:
                SetStatusRaw(
                    LogUiState.Error,
                    FormatLocalize("Loc.Logs.Status.ErrorCodeFormat", error.Code, error.Message));
                break;
        }
    }

    private void UpdateSourceInputs()
    {
        var journal = CurrentSource == ServerLogSource.Journal;
        JournalUnitBox.IsEnabled = journal;
        FilePathBox.IsEnabled = !journal;
    }

    private void UpdateCommandState()
    {
        RefreshButton.IsEnabled = _refreshCancellation is null;
        FollowButton.IsEnabled = _initiallyConnected && !_followRequested && _refreshCancellation is null;
        PauseButton.IsEnabled = _followRequested && !_paused;
        ResumeButton.IsEnabled = _followRequested && _paused;
        CancelButton.IsEnabled = _refreshCancellation is not null || _followRequested;
        ExportButton.IsEnabled = (LogGrid.ItemsSource as IEnumerable<LogEntry>)?.Any() == true;
    }

    private void SetStatusResource(LogUiState state, string messageResourceKey)
    {
        StatusStateText.SetResourceReference(TextBlock.TextProperty, $"Loc.Logs.Status.State.{state}");
        StatusText.SetResourceReference(TextBlock.TextProperty, messageResourceKey);
    }

    private void SetStatusRaw(LogUiState state, string message)
    {
        StatusStateText.SetResourceReference(TextBlock.TextProperty, $"Loc.Logs.Status.State.{state}");
        StatusText.Text = message;
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

    private static string Localize(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as string ?? key;

    private static string FormatLocalize(string key, params object?[] arguments)
    {
        var template = Localize(key);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, arguments);
        }
        catch (FormatException)
        {
            return template;
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

    private enum LogUiState
    {
        Initial,
        Loading,
        Ready,
        Following,
        Paused,
        Empty,
        Disconnected,
        Cancelled,
        Permission,
        Capability,
        NotFound,
        Error,
    }
}
