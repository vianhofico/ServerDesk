using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ServerDesk.Application.Sessions;

namespace ServerDesk.App;

public partial class MainWindow
{
    private const string BackupRestoreActionTag = "ServerDesk.BackupRestore";

    private void EnsureBackupRestoreActionButton()
    {
        var existing = FindDescendant<Button>(this, button =>
            string.Equals(button.Tag as string, BackupRestoreActionTag, StringComparison.Ordinal));
        if (existing is not null)
        {
            return;
        }

        var packageButton = FindDescendant<Button>(this, button =>
            string.Equals(button.Tag as string, PackageAdministrationActionTag, StringComparison.Ordinal));
        if (packageButton is null || VisualTreeHelper.GetParent(packageButton) is not StackPanel actionPanel)
        {
            return;
        }

        var button = new Button
        {
            Tag = BackupRestoreActionTag,
            Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)FindResource("SecondaryButton"),
        };
        button.SetResourceReference(ContentControl.ContentProperty, "Loc.BackupRestore.Action");
        button.SetResourceReference(FrameworkElement.ToolTipProperty, "Loc.BackupRestore.ActionTooltip");
        button.Click += OpenBackupRestoreOnClick;
        var index = actionPanel.Children.IndexOf(packageButton);
        actionPanel.Children.Insert(Math.Min(actionPanel.Children.Count, index + 1), button);
    }

    private void OpenBackupRestoreOnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedServer is not { } selected || System.Windows.Application.Current is not App app)
        {
            return;
        }

        app.OpenBackupRestore(
            selected.Profile,
            selected.ConnectionState == RemoteSessionState.Connected,
            this);
    }
}
