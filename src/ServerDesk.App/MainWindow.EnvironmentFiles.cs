using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ServerDesk.Application.Sessions;

namespace ServerDesk.App;

public partial class MainWindow
{
    private const string EnvironmentFilesActionTag = "ServerDesk.EnvironmentFiles";

    private void EnsureEnvironmentFilesActionButton()
    {
        var existing = FindDescendant<Button>(this, button =>
            string.Equals(button.Tag as string, EnvironmentFilesActionTag, StringComparison.Ordinal));
        if (existing is not null)
        {
            return;
        }

        var tlsButton = FindDescendant<Button>(this, button =>
            string.Equals(button.Tag as string, TlsActionTag, StringComparison.Ordinal));
        if (tlsButton is null || VisualTreeHelper.GetParent(tlsButton) is not StackPanel actionPanel)
        {
            return;
        }

        var envButton = new Button
        {
            Tag = EnvironmentFilesActionTag,
            Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)FindResource("SecondaryButton"),
        };
        envButton.SetResourceReference(ContentControl.ContentProperty, "Loc.Env.WindowTitle");
        envButton.SetResourceReference(FrameworkElement.ToolTipProperty, "Loc.Env.ActionTooltip");
        envButton.Click += OpenEnvironmentFilesOnClick;

        var tlsIndex = actionPanel.Children.IndexOf(tlsButton);
        actionPanel.Children.Insert(Math.Min(actionPanel.Children.Count, tlsIndex + 1), envButton);
    }

    private void OpenEnvironmentFilesOnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedServer is not { } selected)
        {
            return;
        }

        if (System.Windows.Application.Current is not App app)
        {
            return;
        }

        app.OpenEnvironmentFiles(
            selected.Profile,
            selected.ConnectionState == RemoteSessionState.Connected,
            this);
    }
}
