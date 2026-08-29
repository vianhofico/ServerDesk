using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ServerDesk.Application.Sessions;

namespace ServerDesk.App;

public partial class MainWindow
{
    private const string FirewallActionTag = "ServerDesk.Firewall";

    private void EnsureFirewallActionButton()
    {
        var existing = FindDescendant<Button>(this, button =>
            string.Equals(button.Tag as string, FirewallActionTag, StringComparison.Ordinal));
        if (existing is not null)
        {
            return;
        }

        var deploymentButton = FindDescendant<Button>(this, button =>
            string.Equals(button.Tag as string, DeploymentActionTag, StringComparison.Ordinal));
        if (deploymentButton is null || VisualTreeHelper.GetParent(deploymentButton) is not StackPanel actionPanel)
        {
            return;
        }

        var firewallButton = new Button
        {
            Tag = FirewallActionTag,
            Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)FindResource("SecondaryButton"),
        };
        firewallButton.SetResourceReference(ContentControl.ContentProperty, "Loc.Firewall.WindowTitle");
        firewallButton.SetResourceReference(FrameworkElement.ToolTipProperty, "Loc.Firewall.ActionTooltip");
        firewallButton.Click += OpenFirewallOnClick;

        var deploymentIndex = actionPanel.Children.IndexOf(deploymentButton);
        actionPanel.Children.Insert(Math.Min(actionPanel.Children.Count, deploymentIndex + 1), firewallButton);
    }

    private void OpenFirewallOnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedServer is not { } selected ||
            System.Windows.Application.Current is not App app)
        {
            return;
        }

        app.OpenFirewallInventory(
            selected.Profile,
            selected.ConnectionState == RemoteSessionState.Connected,
            this);
    }
}
