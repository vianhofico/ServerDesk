using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ServerDesk.Application.Sessions;

namespace ServerDesk.App;

public partial class MainWindow
{
    private const string GitActionTag = "ServerDesk.GitOperations";

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        EnsureGitActionButton();
        EnsureScheduledTasksActionButton();
    }

    private void EnsureGitActionButton()
    {
        var existing = FindDescendant<Button>(this, button => string.Equals(button.Tag as string, GitActionTag, StringComparison.Ordinal));
        if (existing is not null)
        {
            return;
        }

        var dockerButton = FindDescendant<Button>(this, button => string.Equals(button.Content as string, "Docker", StringComparison.Ordinal));
        if (dockerButton is null || VisualTreeHelper.GetParent(dockerButton) is not StackPanel actionPanel)
        {
            return;
        }

        var gitButton = new Button
        {
            Tag = GitActionTag,
            Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)FindResource("SecondaryButton"),
        };
        gitButton.SetResourceReference(ContentControl.ContentProperty, "Loc.Git.Title");
        gitButton.SetResourceReference(FrameworkElement.ToolTipProperty, "Loc.Git.ActionTooltip");
        gitButton.Click += OpenGitOperationsOnClick;

        var dockerIndex = actionPanel.Children.IndexOf(dockerButton);
        actionPanel.Children.Insert(Math.Min(actionPanel.Children.Count, dockerIndex + 1), gitButton);
    }

    private void OpenGitOperationsOnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedServer is not { } selected)
        {
            return;
        }

        if (System.Windows.Application.Current is not App app)
        {
            return;
        }

        app.OpenGitOperations(
            selected.Profile,
            selected.ConnectionState == RemoteSessionState.Connected,
            this);
    }
}
