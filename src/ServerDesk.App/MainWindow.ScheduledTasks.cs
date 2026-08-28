using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ServerDesk.Application.Sessions;

namespace ServerDesk.App;

public partial class MainWindow
{
    private const string ScheduledTasksActionTag = "ServerDesk.ScheduledTasks";

    private void EnsureScheduledTasksActionButton()
    {
        var existing = FindDescendant<Button>(this, button => string.Equals(button.Tag as string, ScheduledTasksActionTag, StringComparison.Ordinal));
        if (existing is not null)
        {
            return;
        }

        var gitButton = FindDescendant<Button>(this, button => string.Equals(button.Tag as string, GitActionTag, StringComparison.Ordinal));
        if (gitButton is null || VisualTreeHelper.GetParent(gitButton) is not StackPanel actionPanel)
        {
            return;
        }

        var tasksButton = new Button
        {
            Tag = ScheduledTasksActionTag,
            Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)FindResource("SecondaryButton"),
        };
        tasksButton.SetResourceReference(ContentControl.ContentProperty, "Loc.Tasks.WindowTitle");
        tasksButton.SetResourceReference(FrameworkElement.ToolTipProperty, "Loc.Tasks.ActionTooltip");
        tasksButton.Click += OpenScheduledTasksOnClick;

        var gitIndex = actionPanel.Children.IndexOf(gitButton);
        actionPanel.Children.Insert(Math.Min(actionPanel.Children.Count, gitIndex + 1), tasksButton);
    }

    private void OpenScheduledTasksOnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedServer is not { } selected)
        {
            return;
        }

        if (System.Windows.Application.Current is not App app)
        {
            return;
        }

        app.OpenScheduledTasks(
            selected.Profile,
            selected.ConnectionState == RemoteSessionState.Connected,
            this);
    }
}
