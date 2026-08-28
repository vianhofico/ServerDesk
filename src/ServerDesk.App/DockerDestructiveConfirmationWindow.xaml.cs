using System.Windows;
using System.Windows.Controls;

namespace ServerDesk.App;

public partial class DockerDestructiveConfirmationWindow : Window
{
    private readonly string _expectedIdentity;

    public DockerDestructiveConfirmationWindow(
        string action,
        string targetIdentity,
        string consequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(consequence);
        _expectedIdentity = targetIdentity;
        InitializeComponent();

        TitleText.Text = $"Confirm Docker {action}";
        WarningText.Text = consequence;
        PromptText.Text = $"Type '{targetIdentity}' to confirm this exact target:";
    }

    private void ConfirmationOnChanged(object sender, TextChangedEventArgs e)
    {
        ConfirmButton.IsEnabled = string.Equals(
            ConfirmationTextBox.Text,
            _expectedIdentity,
            StringComparison.Ordinal);
    }

    private void ConfirmOnClick(object sender, RoutedEventArgs e)
    {
        if (ConfirmButton.IsEnabled)
        {
            DialogResult = true;
        }
    }
}
