using System.Windows;
using System.Windows.Controls;

namespace ServerDesk.App;

public partial class DockerInventoryWindow
{
    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        if (DiagnosticsButton.Parent is not StackPanel panel)
        {
            return;
        }

        var button = new Button
        {
            Style = (Style)FindResource("DockerButton"),
        };
        button.SetResourceReference(ContentControl.ContentProperty, "Loc.Compose.Projects");
        button.SetResourceReference(FrameworkElement.ToolTipProperty, "Loc.Compose.RuntimeSafety");
        button.Click += OpenComposeOnClick;
        var diagnosticsIndex = panel.Children.IndexOf(DiagnosticsButton);
        panel.Children.Insert(Math.Max(0, diagnosticsIndex + 1), button);
    }

    private void OpenComposeOnClick(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is not App app)
        {
            StatusText.Text = "ServerDesk composition root is unavailable.";
            return;
        }

        app.OpenDockerCompose(_profile, _initiallyConnected, this);
    }
}
