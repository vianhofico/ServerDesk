using ServerDesk.Application.ScheduledTasks;

namespace ServerDesk.App;

public partial class ScheduledTasksWindow
{
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        _localization.LanguageChanged += LocalizationOnLanguageChanged;
        TaskGrid.SelectionChanged += TaskGridLocalizationOnSelectionChanged;
        RefreshLocalizedPresentation();
    }

    protected override void OnClosed(EventArgs e)
    {
        _localization.LanguageChanged -= LocalizationOnLanguageChanged;
        TaskGrid.SelectionChanged -= TaskGridLocalizationOnSelectionChanged;
        base.OnClosed(e);
    }

    private void LocalizationOnLanguageChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(RefreshLocalizedPresentation);
            return;
        }

        RefreshLocalizedPresentation();
    }

    private void TaskGridLocalizationOnSelectionChanged(object? sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        RefreshSelectedTaskPresentation();

    private void RefreshLocalizedPresentation()
    {
        TitleText.Text = _localization.Format("Loc.Tasks.Title", _profile.Name);
        TaskGrid.Items.Refresh();

        if (_snapshot is not null)
        {
            CapabilityText.Text = _localization.Format(
                "Loc.Tasks.Capabilities",
                LocalizedBoolean(_snapshot.CronAvailable),
                LocalizedBoolean(_snapshot.SystemdAvailable));
            if (_snapshot.Warnings.Count > 0)
            {
                CapabilityText.Text += " · " + string.Join(" · ", _snapshot.Warnings);
            }

            StatusText.Text = _snapshot.Tasks.Count == 0
                ? _localization.Get("Loc.Tasks.Empty")
                : _localization.Format("Loc.Tasks.Loaded", _snapshot.Tasks.Count);
        }
        else
        {
            StatusText.Text = _initiallyConnected
                ? _localization.Get("Loc.Tasks.Initial")
                : _localization.Get("Loc.Tasks.Disconnected");
        }

        RefreshSelectedTaskPresentation();
    }

    private void RefreshSelectedTaskPresentation()
    {
        if (TaskGrid.SelectedItem is ScheduledTaskInfo task)
        {
            SelectedTitleText.Text = task.Name;
            SelectedDetailText.Text = _localization.Format(
                "Loc.Tasks.DetailFormat",
                LocalizedKind(task.Kind),
                task.Schedule,
                LocalizedBoolean(task.Enabled),
                LocalizedBoolean(task.Active),
                task.LastRun ?? "—",
                task.NextRun ?? "—");
        }
        else
        {
            SelectedTitleText.Text = _localization.Get("Loc.Tasks.SelectTask");
        }
    }

    private string LocalizedKind(ScheduledTaskKind kind) =>
        _localization.Get(kind == ScheduledTaskKind.Cron
            ? "Loc.Tasks.Kind.Cron"
            : "Loc.Tasks.Kind.SystemdTimer");

    private string LocalizedBoolean(bool value) =>
        _localization.Get(value ? "Loc.Common.Yes" : "Loc.Common.No");

    private string LocalizedBoolean(bool? value) =>
        value.HasValue ? LocalizedBoolean(value.Value) : "—";
}
