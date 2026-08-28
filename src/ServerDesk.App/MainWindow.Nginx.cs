using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ServerDesk.Application.Sessions;

namespace ServerDesk.App;

public partial class MainWindow
{
    private const string NginxActionTag = "ServerDesk.NginxInventory";

    private void EnsureNginxActionButton()
    {
        var existing = FindDescendant<Button>(this, button => string.Equals(button.Tag as string, NginxActionTag, StringComparison.Ordinal));
        if (existing is not null)
        {
            return;
        }

        var tasksButton = FindDescendant<Button>(this, button => string.Equals(button.Tag as string, ScheduledTasksActionTag, StringComparison.Ordinal));
        if (tasksButton is null || VisualTreeHelper.GetParent(tasksButton) is not StackPanel actionPanel)
        {
            return;
        }

        var nginxButton = new Button
        {
            Tag = NginxActionTag,
            Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)FindResource("SecondaryButton"),
        };
        nginxButton.SetResourceReference(ContentControl.ContentProperty, "Loc.Nginx.WindowTitle");
        nginxButton.SetResourceReference(FrameworkElement.ToolTipProperty, "Loc.Nginx.ActionTooltip");
        nginxButton.Click += OpenNginxInventoryOnClick;

        var tasksIndex = actionPanel.Children.IndexOf(tasksButton);
        actionPanel.Children.Insert(Math.Min(actionPanel.Children.Count, tasksIndex + 1), nginxButton);
    }

    private void OpenNginxInventoryOnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedServer is not { } selected)
        {
            return;
        }

        if (System.Windows.Application.Current is not App app)
        {
            return;
        }

        app.OpenNginxInventory(
            selected.Profile,
            selected.ConnectionState == RemoteSessionState.Connected,
            this);
    }
}
