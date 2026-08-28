using System.Windows;
using System.Windows.Controls;
using ServerDesk.App.Localization;
using ServerDesk.Application.ScheduledTasks;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class ScheduledTasksWindow : Window
{
    private readonly IScheduledTaskService _service;
    private readonly ILocalizationService _localization;
    private readonly ServerProfile _profile;
    private readonly bool _initiallyConnected;
    private readonly List<ScheduledTaskInfo> _allTasks = [];
    private CancellationTokenSource? _activeCancellation;
    private ScheduledTaskSnapshot? _snapshot;
    private string? _editingCronId;
    private bool _mutationActive;

    public ScheduledTasksWindow(
        IScheduledTaskService service,
        ILocalizationService localization,
        ServerProfile profile,
        bool connected)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _initiallyConnected = connected;
        InitializeComponent();
        Title = _localization.Get("Loc.Tasks.WindowTitle");
        TitleText.Text = _localization.Format("Loc.Tasks.Title", profile.Name);
        EndpointText.Text = $"{profile.Username}@{profile.Host}:{profile.Port}";
        StatusText.Text = connected
            ? _localization.Get("Loc.Tasks.Initial")
            : _localization.Get("Loc.Tasks.Disconnected");
        SetEditorEnabled(false);
    }

    private async void WindowOnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initiallyConnected)
        {
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    private void WindowOnClosed(object? sender, EventArgs e)
    {
        _activeCancellation?.Cancel();
        _activeCancellation?.Dispose();
        _activeCancellation = null;
    }

    private async void RefreshOnClick(object sender, RoutedEventArgs e) => await RefreshAsync().ConfigureAwait(true);

    private void CancelOnClick(object sender, RoutedEventArgs e)
    {
        _activeCancellation?.Cancel();
        StatusText.Text = _mutationActive
            ? _localization.Get("Loc.Tasks.CancelledMutation")
            : _localization.Get("Loc.Tasks.Cancelled");
    }

    private async Task RefreshAsync()
    {
        if (!_initiallyConnected)
        {
            StatusText.Text = _localization.Get("Loc.Tasks.Disconnected");
            return;
        }

        using var scope = BeginOperation();
        try
        {
            StatusText.Text = _localization.Get("Loc.Tasks.Loading");
            var result = await _service.InspectAsync(_profile, scope.Token).ConfigureAwait(true);
            if (!result.IsSuccess || result.Snapshot is null)
            {
                ApplyError(result.Error);
                return;
            }

            ApplySnapshot(result.Snapshot);
            StatusText.Text = result.Snapshot.Tasks.Count == 0
                ? _localization.Get("Loc.Tasks.Empty")
                : _localization.Format("Loc.Tasks.Loaded", result.Snapshot.Tasks.Count);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = _localization.Get("Loc.Tasks.Cancelled");
        }
    }

    private void ApplySnapshot(ScheduledTaskSnapshot snapshot)
    {
        _snapshot = snapshot;
        _allTasks.Clear();
        _allTasks.AddRange(snapshot.Tasks);
        ApplyFilter();
        RawCronBox.Text = snapshot.RawCrontab;
        ApplyRawCronButton.IsEnabled = snapshot.CronAvailable;
        CapabilityText.Text = _localization.Format(
            "Loc.Tasks.Capabilities",
            snapshot.CronAvailable ? _localization.Get("Loc.Common.Yes") : _localization.Get("Loc.Common.No"),
            snapshot.SystemdAvailable ? _localization.Get("Loc.Common.Yes") : _localization.Get("Loc.Common.No"));
        if (snapshot.Warnings.Count > 0)
        {
            CapabilityText.Text += " · " + string.Join(" · ", snapshot.Warnings);
        }
    }

    private void SearchBoxOnTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        TaskGrid.ItemsSource = query.Length == 0
            ? _allTasks.ToArray()
            : _allTasks.Where(task =>
                    task.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    task.Schedule.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    task.CommandOrUnit.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToArray();
    }

    private void TaskGridOnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TaskGrid.SelectedItem is not ScheduledTaskInfo task)
        {
            SelectedTitleText.Text = _localization.Get("Loc.Tasks.SelectTask");
            SelectedDetailText.Text = string.Empty;
            EnableButton.IsEnabled = false;
            DisableButton.IsEnabled = false;
            DeleteButton.IsEnabled = false;
            HistoryButton.IsEnabled = false;
            RawSourceButton.IsEnabled = false;
            SetEditorEnabled(false);
            return;
        }

        SelectedTitleText.Text = task.Name;
        SelectedDetailText.Text = _localization.Format(
            "Loc.Tasks.DetailFormat",
            task.Kind,
            task.Schedule,
            task.Enabled ? _localization.Get("Loc.Common.Yes") : _localization.Get("Loc.Common.No"),
            task.Active?.ToString() ?? "—",
            task.LastRun ?? "—",
            task.NextRun ?? "—");
        EnableButton.IsEnabled = !task.Enabled;
        DisableButton.IsEnabled = task.Enabled;
        DeleteButton.IsEnabled = task.Kind == ScheduledTaskKind.Cron ||
                                 task.SourcePath?.StartsWith("/etc/systemd/system/", StringComparison.Ordinal) == true;
        HistoryButton.IsEnabled = task.Kind == ScheduledTaskKind.SystemdTimer;
        RawSourceButton.IsEnabled = true;

        if (task.Kind == ScheduledTaskKind.Cron && task.IsSimpleEditable)
        {
            var fields = task.Schedule.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 5)
            {
                _editingCronId = task.Id;
                MinuteBox.Text = fields[0];
                HourBox.Text = fields[1];
                DayOfMonthBox.Text = fields[2];
                MonthBox.Text = fields[3];
                DayOfWeekBox.Text = fields[4];
                CommandBox.Text = task.CommandOrUnit;
                CronEnabledCheck.IsChecked = task.Enabled;
                SetEditorEnabled(_snapshot?.CronAvailable == true);
                return;
            }
        }

        _editingCronId = null;
        SetEditorEnabled(false);
    }

    private void NewCronOnClick(object sender, RoutedEventArgs e)
    {
        if (_snapshot?.CronAvailable != true)
        {
            StatusText.Text = _localization.Get("Loc.Tasks.CronUnavailable");
            return;
        }

        TaskGrid.SelectedItem = null;
        _editingCronId = null;
        MinuteBox.Text = "0";
        HourBox.Text = "0";
        DayOfMonthBox.Text = "*";
        MonthBox.Text = "*";
        DayOfWeekBox.Text = "*";
        CommandBox.Text = string.Empty;
        CronEnabledCheck.IsChecked = true;
        SetEditorEnabled(true);
        CommandBox.Focus();
        StatusText.Text = _localization.Get("Loc.Tasks.NewCronReady");
    }

    private async void SaveCronOnClick(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null)
        {
            return;
        }

        CronTaskDraft draft;
        try
        {
            draft = new CronTaskDraft(
                _editingCronId,
                MinuteBox.Text,
                HourBox.Text,
                DayOfMonthBox.Text,
                MonthBox.Text,
                DayOfWeekBox.Text,
                CommandBox.Text,
                CronEnabledCheck.IsChecked == true);
            draft.Validate();
        }
        catch (FormatException exception)
        {
            StatusText.Text = _localization.Format("Loc.Tasks.ValidationError", exception.Message);
            return;
        }

        if (!Confirm(_editingCronId is null ? "Loc.Tasks.Action.Create" : "Loc.Tasks.Action.Update", draft.Command))
        {
            StatusText.Text = _localization.Get("Loc.Tasks.ActionNotRun");
            return;
        }

        await RunMutationAsync(
            token => _service.SaveCronAsync(_profile, draft, _snapshot.RawCrontab, token),
            _localization.Get("Loc.Tasks.SavingCron"))
            .ConfigureAwait(true);
    }

    private async void EnableOnClick(object sender, RoutedEventArgs e) => await SetEnabledAsync(true).ConfigureAwait(true);

    private async void DisableOnClick(object sender, RoutedEventArgs e) => await SetEnabledAsync(false).ConfigureAwait(true);

    private async Task SetEnabledAsync(bool enabled)
    {
        if (_snapshot is null || TaskGrid.SelectedItem is not ScheduledTaskInfo task)
        {
            return;
        }

        if (!Confirm(enabled ? "Loc.Tasks.Action.Enable" : "Loc.Tasks.Action.Disable", task.Name))
        {
            StatusText.Text = _localization.Get("Loc.Tasks.ActionNotRun");
            return;
        }

        await RunMutationAsync(
            token => _service.SetEnabledAsync(_profile, task, enabled, _snapshot.RawCrontab, token),
            _localization.Format("Loc.Tasks.ChangingState", task.Name))
            .ConfigureAwait(true);
    }

    private async void DeleteOnClick(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null || TaskGrid.SelectedItem is not ScheduledTaskInfo task)
        {
            return;
        }

        var result = MessageBox.Show(
            _localization.Format("Loc.Tasks.ConfirmDelete", task.Name),
            _localization.Get("Loc.Tasks.ConfirmDeleteTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
        {
            StatusText.Text = _localization.Get("Loc.Tasks.ActionNotRun");
            return;
        }

        await RunMutationAsync(
            token => _service.DeleteAsync(_profile, task, _snapshot.RawCrontab, token),
            _localization.Format("Loc.Tasks.Deleting", task.Name))
            .ConfigureAwait(true);
    }

    private async void ApplyRawCronOnClick(object sender, RoutedEventArgs e)
    {
        if (_snapshot is null)
        {
            return;
        }

        var result = MessageBox.Show(
            _localization.Get("Loc.Tasks.ConfirmRawCron"),
            _localization.Get("Loc.Tasks.ConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
        {
            StatusText.Text = _localization.Get("Loc.Tasks.ActionNotRun");
            return;
        }

        try
        {
            await RunMutationAsync(
                token => _service.ApplyRawCrontabAsync(_profile, RawCronBox.Text, _snapshot.RawCrontab, token),
                _localization.Get("Loc.Tasks.ApplyingRawCron"))
                .ConfigureAwait(true);
        }
        catch (FormatException exception)
        {
            StatusText.Text = _localization.Format("Loc.Tasks.ValidationError", exception.Message);
        }
    }

    private async void HistoryOnClick(object sender, RoutedEventArgs e)
    {
        if (TaskGrid.SelectedItem is not ScheduledTaskInfo task)
        {
            return;
        }

        using var scope = BeginOperation();
        try
        {
            StatusText.Text = _localization.Format("Loc.Tasks.LoadingHistory", task.Name);
            var result = await _service.ReadHistoryAsync(_profile, task, scope.Token).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                ApplyError(result.Error);
                return;
            }

            HistoryBox.Text = result.Text;
            StatusText.Text = _localization.Get("Loc.Tasks.HistoryLoaded");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = _localization.Get("Loc.Tasks.Cancelled");
        }
    }

    private async void RawSourceOnClick(object sender, RoutedEventArgs e)
    {
        if (TaskGrid.SelectedItem is not ScheduledTaskInfo task)
        {
            return;
        }

        using var scope = BeginOperation();
        try
        {
            StatusText.Text = _localization.Format("Loc.Tasks.LoadingRawSource", task.Name);
            var result = await _service.ReadRawSourceAsync(_profile, task, scope.Token).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                ApplyError(result.Error);
                return;
            }

            RawSourceBox.Text = result.Text;
            StatusText.Text = _localization.Get("Loc.Tasks.RawSourceLoaded");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = _localization.Get("Loc.Tasks.Cancelled");
        }
    }

    private async Task RunMutationAsync(
        Func<CancellationToken, Task<ScheduledTaskMutationResult>> mutation,
        string runningStatus)
    {
        using var scope = BeginOperation();
        _mutationActive = true;
        try
        {
            StatusText.Text = runningStatus;
            var result = await mutation(scope.Token).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                ApplyError(result.Error);
                return;
            }

            if (result.VerifiedSnapshot is not null)
            {
                ApplySnapshot(result.VerifiedSnapshot);
            }
            else
            {
                await RefreshAsync().ConfigureAwait(true);
            }

            StatusText.Text = _localization.Get("Loc.Tasks.ActionVerified");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = _localization.Get("Loc.Tasks.CancelledMutation");
        }
        finally
        {
            _mutationActive = false;
        }
    }

    private bool Confirm(string actionKey, string target)
    {
        var action = _localization.Get(actionKey);
        return MessageBox.Show(
                   _localization.Format("Loc.Tasks.ConfirmBody", action, target),
                   _localization.Get("Loc.Tasks.ConfirmTitle"),
                   MessageBoxButton.YesNo,
                   MessageBoxImage.Warning,
                   MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private void ApplyError(RemoteError? error)
    {
        if (error is null)
        {
            StatusText.Text = _localization.Get("Loc.Tasks.UnknownError");
            return;
        }

        StatusText.Text = error.Code switch
        {
            RemoteErrorCode.AmbiguousState => _localization.Format("Loc.Tasks.Ambiguous", error.Message),
            RemoteErrorCode.PermissionDenied or RemoteErrorCode.SudoRequired => _localization.Format("Loc.Tasks.Permission", error.Message),
            RemoteErrorCode.CapabilityUnavailable or RemoteErrorCode.CommandNotFound => _localization.Format("Loc.Tasks.CapabilityError", error.Message),
            RemoteErrorCode.PathConflict => _localization.Format("Loc.Tasks.Conflict", error.Message),
            RemoteErrorCode.ParseFailed => _localization.Format("Loc.Tasks.ParseError", error.Message),
            _ => _localization.Format("Loc.Tasks.Error", error.Message),
        };
    }

    private void SetEditorEnabled(bool enabled)
    {
        MinuteBox.IsEnabled = enabled;
        HourBox.IsEnabled = enabled;
        DayOfMonthBox.IsEnabled = enabled;
        MonthBox.IsEnabled = enabled;
        DayOfWeekBox.IsEnabled = enabled;
        CommandBox.IsEnabled = enabled;
        CronEnabledCheck.IsEnabled = enabled;
        SaveCronButton.IsEnabled = enabled;
    }

    private OperationScope BeginOperation()
    {
        _activeCancellation?.Cancel();
        _activeCancellation?.Dispose();
        _activeCancellation = new CancellationTokenSource();
        return new OperationScope(this, _activeCancellation);
    }

    private sealed class OperationScope : IDisposable
    {
        private readonly ScheduledTasksWindow _owner;
        private readonly CancellationTokenSource _source;
        private bool _disposed;

        public OperationScope(ScheduledTasksWindow owner, CancellationTokenSource source)
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
            if (ReferenceEquals(_owner._activeCancellation, _source))
            {
                _owner._activeCancellation = null;
            }

            _source.Dispose();
        }
    }
}
