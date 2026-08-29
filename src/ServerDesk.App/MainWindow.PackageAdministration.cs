using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ServerDesk.Application.Sessions;

namespace ServerDesk.App;

public partial class MainWindow
{
    private const string PackageAdministrationActionTag = "ServerDesk.PackageAdministration";

    private void EnsurePackageAdministrationActionButton()
    {
        var existing = FindDescendant<Button>(this, button =>
            string.Equals(button.Tag as string, PackageAdministrationActionTag, StringComparison.Ordinal));
        if (existing is not null)
        {
            return;
        }

        var userButton = FindDescendant<Button>(this, button =>
            string.Equals(button.Tag as string, UserAdministrationActionTag, StringComparison.Ordinal));
        if (userButton is null || VisualTreeHelper.GetParent(userButton) is not StackPanel actionPanel)
        {
            return;
        }

        var button = new Button
        {
            Tag = PackageAdministrationActionTag,
            Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)FindResource("SecondaryButton"),
        };
        button.SetResourceReference(ContentControl.ContentProperty, "Loc.PackageAdmin.Action");
        button.SetResourceReference(FrameworkElement.ToolTipProperty, "Loc.PackageAdmin.ActionTooltip");
        button.Click += OpenPackageAdministrationOnClick;

        var userIndex = actionPanel.Children.IndexOf(userButton);
        actionPanel.Children.Insert(Math.Min(actionPanel.Children.Count, userIndex + 1), button);
    }

    private void OpenPackageAdministrationOnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedServer is not { } selected ||
            System.Windows.Application.Current is not App app)
        {
            return;
        }

        app.OpenPackageAdministration(
            selected.Profile,
            selected.ConnectionState == RemoteSessionState.Connected,
            this);
    }
}
