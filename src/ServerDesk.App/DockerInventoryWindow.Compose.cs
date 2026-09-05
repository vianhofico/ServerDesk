using System.Windows;
using System.Windows.Controls;

namespace ServerDesk.App;

public partial class DockerInventoryWindow
{
    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        if (DiagnosticsButton.Parent is not Panel parent)
        {
            return;
        }

        var button = new Button
        {
            Style = (Style)FindResource("DockerButton"),
            Margin = new Thickness(6, 0, 0, 0),
        };
        button.SetResourceReference(ContentControl.ContentProperty, "Loc.Compose.Projects");
        button.SetResourceReference(FrameworkElement.ToolTipProperty, "Loc.Compose.RuntimeSafety");
        button.Click += OpenComposeOnClick;

        if (parent is StackPanel panel)
        {
            var diagnosticsIndex = panel.Children.IndexOf(DiagnosticsButton);
            panel.Children.Insert(Math.Max(0, diagnosticsIndex + 1), button);
            return;
        }

        if (parent is Grid grid)
        {
            var column = Grid.GetColumn(DiagnosticsButton);
            var row = Grid.GetRow(DiagnosticsButton);
            grid.Children.Remove(DiagnosticsButton);

            var actionPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
            };
            Grid.SetColumn(actionPanel, column);
            Grid.SetRow(actionPanel, row);
            actionPanel.Children.Add(DiagnosticsButton);
            actionPanel.Children.Add(button);
            grid.Children.Add(actionPanel);
        }
    }

    private void OpenComposeOnClick(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is not App app)
        {
            StatusText.SetResourceReference(TextBlock.TextProperty, "Loc.Compose.UnknownError");
            return;
        }

        app.OpenDockerCompose(_profile, _hasConnection, this);
    }
}
