using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ServerDesk.Application.Sessions;

namespace ServerDesk.App;

public partial class MainWindow
{
    private const string UserAdministrationActionTag = "ServerDesk.UserAdministration";

    private void EnsureUserAdministrationActionButton()
    {
        var existing = FindDescendant<Button>(this, button =>
            string.Equals(button.Tag as string, UserAdministrationActionTag, StringComparison.Ordinal));
        if (existing is not null)
        {
            return;
        }

        var firewallButton = FindDescendant<Button>(this, button =>
            string.Equals(button.Tag as string, FirewallActionTag, StringComparison.Ordinal));
        if (firewallButton is null || VisualTreeHelper.GetParent(firewallButton) is not StackPanel actionPanel)
        {
            return;
        }

        var button = new Button
        {
            Tag = UserAdministrationActionTag,
            Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)FindResource("SecondaryButton"),
        };
        button.SetResourceReference(ContentControl.ContentProperty, "Loc.UserAdmin.Action");
        button.SetResourceReference(FrameworkElement.ToolTipProperty, "Loc.UserAdmin.ActionTooltip");
        button.Click += OpenUserAdministrationOnClick;

        var firewallIndex = actionPanel.Children.IndexOf(firewallButton);
        actionPanel.Children.Insert(Math.Min(actionPanel.Children.Count, firewallIndex + 1), button);
    }

    private void OpenUserAdministrationOnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedServer is not { } selected ||
            System.Windows.Application.Current is not App app)
        {
            return;
        }

        app.OpenUserAdministration(
            selected.Profile,
            selected.ConnectionState == RemoteSessionState.Connected,
            this);
    }
}
