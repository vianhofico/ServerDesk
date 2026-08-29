using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ServerDesk.App;

public partial class MainWindow
{
    private const string OperationHistoryActionTag = "ServerDesk.OperationHistory";

    private void EnsureOperationHistoryActionButton()
    {
        var existing = FindDescendant<Button>(this, button =>
            string.Equals(button.Tag as string, OperationHistoryActionTag, StringComparison.Ordinal));
        if (existing is not null)
        {
            return;
        }

        var backupButton = FindDescendant<Button>(this, button =>
            string.Equals(button.Tag as string, BackupRestoreActionTag, StringComparison.Ordinal));
        if (backupButton is null || VisualTreeHelper.GetParent(backupButton) is not StackPanel actionPanel)
        {
            return;
        }

        var button = new Button
        {
            Tag = OperationHistoryActionTag,
            Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)FindResource("SecondaryButton"),
        };
        button.SetResourceReference(ContentControl.ContentProperty, "Loc.OperationHistory.Action");
        button.SetResourceReference(FrameworkElement.ToolTipProperty, "Loc.OperationHistory.ActionTooltip");
        button.Click += OpenOperationHistoryOnClick;
        var index = actionPanel.Children.IndexOf(backupButton);
        actionPanel.Children.Insert(Math.Min(actionPanel.Children.Count, index + 1), button);
    }

    private void OpenOperationHistoryOnClick(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is not App app)
        {
            return;
        }

        app.OpenOperationHistory(_viewModel.SelectedServer?.Profile.Id, this);
    }
}
