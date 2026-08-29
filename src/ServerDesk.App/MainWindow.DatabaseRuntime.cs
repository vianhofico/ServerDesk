using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ServerDesk.Application.Sessions;

namespace ServerDesk.App;

public partial class MainWindow
{
    private const string DatabaseRuntimeActionTag = "ServerDesk.DatabaseRuntime";
    private const string DatabaseProfilesActionTag = "ServerDesk.DatabaseProfiles";

    private void EnsureDatabaseRuntimeActionButton()
    {
        var runtimeButton = FindDescendant<Button>(this, button =>
            string.Equals(button.Tag as string, DatabaseRuntimeActionTag, StringComparison.Ordinal));
        StackPanel? actionPanel;
        if (runtimeButton is null)
        {
            var historyButton = FindDescendant<Button>(this, button =>
                string.Equals(button.Tag as string, OperationHistoryActionTag, StringComparison.Ordinal));
            if (historyButton is null || VisualTreeHelper.GetParent(historyButton) is not StackPanel historyPanel)
            {
                return;
            }

            actionPanel = historyPanel;
            runtimeButton = new Button
            {
                Tag = DatabaseRuntimeActionTag,
                Margin = new Thickness(8, 0, 0, 0),
                Style = (Style)FindResource("SecondaryButton"),
            };
            runtimeButton.SetResourceReference(ContentControl.ContentProperty, "Loc.DatabaseRuntime.Action");
            runtimeButton.SetResourceReference(FrameworkElement.ToolTipProperty, "Loc.DatabaseRuntime.ActionTooltip");
            runtimeButton.Click += OpenDatabaseRuntimeOnClick;
            var historyIndex = actionPanel.Children.IndexOf(historyButton);
            actionPanel.Children.Insert(Math.Min(actionPanel.Children.Count, historyIndex + 1), runtimeButton);
        }
        else
        {
            actionPanel = VisualTreeHelper.GetParent(runtimeButton) as StackPanel;
        }

        EnsureDatabaseProfilesActionButton(actionPanel, runtimeButton);
    }

    private void EnsureDatabaseProfilesActionButton(StackPanel? actionPanel, Button runtimeButton)
    {
        if (actionPanel is null)
        {
            return;
        }

        var existing = FindDescendant<Button>(this, button =>
            string.Equals(button.Tag as string, DatabaseProfilesActionTag, StringComparison.Ordinal));
        if (existing is not null)
        {
            return;
        }

        var button = new Button
        {
            Tag = DatabaseProfilesActionTag,
            Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)FindResource("SecondaryButton"),
        };
        button.SetResourceReference(ContentControl.ContentProperty, "Loc.DatabaseProfiles.Action");
        button.SetResourceReference(FrameworkElement.ToolTipProperty, "Loc.DatabaseProfiles.ActionTooltip");
        button.Click += OpenDatabaseProfilesOnClick;
        var runtimeIndex = actionPanel.Children.IndexOf(runtimeButton);
        actionPanel.Children.Insert(Math.Min(actionPanel.Children.Count, runtimeIndex + 1), button);
    }

    private void OpenDatabaseRuntimeOnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedServer is not { } selected || System.Windows.Application.Current is not App app)
        {
            return;
        }

        app.OpenDatabaseRuntime(
            selected.Profile,
            selected.ConnectionState == RemoteSessionState.Connected,
            this);
    }

    private void OpenDatabaseProfilesOnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedServer is not { } selected || System.Windows.Application.Current is not App app)
        {
            return;
        }

        app.OpenDatabaseProfiles(selected.Profile, this);
    }
}
