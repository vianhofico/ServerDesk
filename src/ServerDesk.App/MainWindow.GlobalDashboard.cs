using System.Windows;
using System.Windows.Controls;
using ServerDesk.Application.Sessions;

namespace ServerDesk.App;

public partial class MainWindow
{
    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Loaded += AddGlobalDashboardActionOnLoaded;
    }

    private void AddGlobalDashboardActionOnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= AddGlobalDashboardActionOnLoaded;

        var addButton = FindDescendant<Button>(
            this,
            button => ReferenceEquals(button.Command, _viewModel.AddServerCommand));
        if (addButton is null || addButton.Parent is not Grid headerGrid)
        {
            return;
        }

        headerGrid.Children.Remove(addButton);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(actions, 1);

        var globalDashboardButton = new Button
        {
            Margin = new Thickness(0, 0, 8, 0),
            Style = (Style)FindResource("SecondaryButton"),
        };
        globalDashboardButton.SetResourceReference(ContentControl.ContentProperty, "Loc.GlobalDashboard.Action");
        globalDashboardButton.SetResourceReference(FrameworkElement.ToolTipProperty, "Loc.GlobalDashboard.ActionTooltip");
        globalDashboardButton.Click += OpenGlobalDashboardOnClick;

        actions.Children.Add(globalDashboardButton);
        actions.Children.Add(addButton);
        headerGrid.Children.Add(actions);
    }

    private void OpenGlobalDashboardOnClick(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is not App app)
        {
            return;
        }

        app.OpenGlobalDashboard(
            () => _viewModel.Servers
                .Select(server => new GlobalDashboardTarget(server.Profile, server.ConnectionState))
                .ToArray(),
            this);
    }
}
