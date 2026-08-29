using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ServerDesk.Application.Sessions;

namespace ServerDesk.App;

public partial class MainWindow
{
    private const string DatabaseRuntimeActionTag = "ServerDesk.DatabaseRuntime";

    private void EnsureDatabaseRuntimeActionButton()
    {
        var existing = FindDescendant<Button>(this, button =>
            string.Equals(button.Tag as string, DatabaseRuntimeActionTag, StringComparison.Ordinal));
        if (existing is not null)
        {
            return;
        }

        var historyButton = FindDescendant<Button>(this, button =>
            string.Equals(button.Tag as string, OperationHistoryActionTag, StringComparison.Ordinal));
        if (historyButton is null || VisualTreeHelper.GetParent(historyButton) is not StackPanel actionPanel)
        {
            return;
        }

        var button = new Button
        {
            Tag = DatabaseRuntimeActionTag,
            Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)FindResource("SecondaryButton"),
        };
        button.SetResourceReference(ContentControl.ContentProperty, "Loc.DatabaseRuntime.Action");
        button.SetResourceReference(FrameworkElement.ToolTipProperty, "Loc.DatabaseRuntime.ActionTooltip");
        button.Click += OpenDatabaseRuntimeOnClick;
        var index = actionPanel.Children.IndexOf(historyButton);
        actionPanel.Children.Insert(Math.Min(actionPanel.Children.Count, index + 1), button);
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
}
