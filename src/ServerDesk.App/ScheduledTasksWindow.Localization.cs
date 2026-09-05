using System.Windows.Controls;
using ServerDesk.Application.ScheduledTasks;

namespace ServerDesk.App;

public partial class ScheduledTasksWindow
{
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        _localization.LanguageChanged += LocalizationOnLanguageChanged;
        RefreshLocalizedPresentation();
        RefreshOverlay();
    }

    protected override void OnClosed(EventArgs e)
    {
        _localization.LanguageChanged -= LocalizationOnLanguageChanged;
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

    private void RefreshLocalizedPresentation()
    {
        Title = _localization.Get("Loc.Tasks.WindowTitle");
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
        }

        RefreshFooterPresentation();
        RefreshSelectedTaskPresentation();
        RefreshOverlay();
    }

    private void RefreshFooterPresentation()
    {
        if (!_hasLoadedSnapshot)
        {
            FooterText.SetResourceReference(TextBlock.TextProperty, "Loc.TasksWorkspace.Footer.None");
            return;
        }

        var query = SearchBox.Text;
        var visibleCount = TaskGrid.Items.Count;
        FooterText.Text = string.IsNullOrWhiteSpace(query)
            ? _localization.Format("Loc.TasksWorkspace.Footer.All", visibleCount)
            : _localization.Format(
                "Loc.TasksWorkspace.Footer.Filtered",
                visibleCount,
                _allTasks.Count,
                query.Trim());
    }

    private void RefreshSelectedTaskPresentation()
    {
        if (TaskGrid.SelectedItem is not ScheduledTaskInfo task)
        {
            return;
        }

        DetailKindText.Text = LocalizedKind(task.Kind);
        DetailEnabledText.Text = LocalizedBoolean(task.Enabled);
        DetailActiveText.Text = LocalizedBoolean(task.Active);
        DetailEditableText.Text = LocalizedBoolean(task.Kind == ScheduledTaskKind.Cron && task.IsSimpleEditable);
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
