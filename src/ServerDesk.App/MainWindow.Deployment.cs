using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ServerDesk.Application.Sessions;

namespace ServerDesk.App;

public partial class MainWindow
{
    private const string DeploymentActionTag = "ServerDesk.Deployment";

    private void EnsureDeploymentActionButton()
    {
        var existing = FindDescendant<Button>(this, button =>
            string.Equals(button.Tag as string, DeploymentActionTag, StringComparison.Ordinal));
        if (existing is not null)
        {
            return;
        }

        var environmentButton = FindDescendant<Button>(this, button =>
            string.Equals(button.Tag as string, EnvironmentFilesActionTag, StringComparison.Ordinal));
        if (environmentButton is null || VisualTreeHelper.GetParent(environmentButton) is not StackPanel actionPanel)
        {
            return;
        }

        var deploymentButton = new Button
        {
            Tag = DeploymentActionTag,
            Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)FindResource("SecondaryButton"),
        };
        deploymentButton.SetResourceReference(ContentControl.ContentProperty, "Loc.Deploy.Title");
        deploymentButton.SetResourceReference(FrameworkElement.ToolTipProperty, "Loc.Deploy.ActionTooltip");
        deploymentButton.Click += OpenDeploymentOnClick;

        var environmentIndex = actionPanel.Children.IndexOf(environmentButton);
        actionPanel.Children.Insert(Math.Min(actionPanel.Children.Count, environmentIndex + 1), deploymentButton);
    }

    private void OpenDeploymentOnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedServer is not { } selected)
        {
            return;
        }

        if (System.Windows.Application.Current is not App app)
        {
            return;
        }

        app.OpenDeployment(
            selected.Profile,
            selected.ConnectionState == RemoteSessionState.Connected,
            this);
    }
}
