using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ServerDesk.Application.Sessions;

namespace ServerDesk.App;

public partial class MainWindow
{
    private const string TlsActionTag = "ServerDesk.TlsCertificates";

    private void EnsureTlsActionButton()
    {
        var existing = FindDescendant<Button>(this, button => string.Equals(button.Tag as string, TlsActionTag, StringComparison.Ordinal));
        if (existing is not null)
        {
            return;
        }

        var nginxButton = FindDescendant<Button>(this, button => string.Equals(button.Tag as string, NginxActionTag, StringComparison.Ordinal));
        if (nginxButton is null || VisualTreeHelper.GetParent(nginxButton) is not StackPanel actionPanel)
        {
            return;
        }

        var tlsButton = new Button
        {
            Tag = TlsActionTag,
            Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)FindResource("SecondaryButton"),
        };
        tlsButton.SetResourceReference(ContentControl.ContentProperty, "Loc.Tls.WindowTitle");
        tlsButton.SetResourceReference(FrameworkElement.ToolTipProperty, "Loc.Tls.ActionTooltip");
        tlsButton.Click += OpenTlsCertificatesOnClick;

        var nginxIndex = actionPanel.Children.IndexOf(nginxButton);
        actionPanel.Children.Insert(Math.Min(actionPanel.Children.Count, nginxIndex + 1), tlsButton);
    }

    private void OpenTlsCertificatesOnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedServer is not { } selected)
        {
            return;
        }

        if (System.Windows.Application.Current is not App app)
        {
            return;
        }

        app.OpenTlsCertificates(
            selected.Profile,
            selected.ConnectionState == RemoteSessionState.Connected,
            this);
    }
}
