using System.Windows;
using System.Windows.Controls;
using ServerDesk.Application.Sessions;

namespace ServerDesk.App;

public partial class InteractiveAuthenticationDialog : Window
{
    private readonly List<Control> _responseControls = [];

    public InteractiveAuthenticationDialog(InteractiveAuthenticationChallenge challenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        InitializeComponent();

        UsernameText.Text = $"Authenticating as {challenge.Username}";
        InstructionText.Text = string.IsNullOrWhiteSpace(challenge.Instruction)
            ? "The SSH server is requesting additional authentication information."
            : challenge.Instruction;

        foreach (var prompt in challenge.Prompts)
        {
            AddPrompt(prompt);
        }
    }

    public IReadOnlyList<string>? Responses { get; private set; }

    private void AddPrompt(InteractiveAuthenticationPrompt prompt)
    {
        var container = new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 14),
        };
        container.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(prompt.Request) ? "Response" : prompt.Request,
            Margin = new Thickness(0, 0, 0, 5),
            TextWrapping = TextWrapping.Wrap,
        });

        Control input;
        if (prompt.IsSecret)
        {
            input = new PasswordBox
            {
                MinHeight = 36,
                Padding = new Thickness(10, 6, 10, 6),
            };
        }
        else
        {
            input = new TextBox
            {
                MinHeight = 36,
                Padding = new Thickness(10, 6, 10, 6),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
        }

        _responseControls.Add(input);
        container.Children.Add(input);
        PromptPanel.Children.Add(container);
    }

    private void ContinueButtonOnClick(object sender, RoutedEventArgs e)
    {
        Responses = _responseControls
            .Select(control => control switch
            {
                PasswordBox passwordBox => passwordBox.Password,
                TextBox textBox => textBox.Text,
                _ => string.Empty,
            })
            .ToArray();
        ClearSecretControls();
        DialogResult = true;
    }

    private void CancelButtonOnClick(object sender, RoutedEventArgs e)
    {
        Responses = null;
        ClearSecretControls();
        DialogResult = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        ClearSecretControls();
        base.OnClosed(e);
    }

    private void ClearSecretControls()
    {
        foreach (var passwordBox in _responseControls.OfType<PasswordBox>())
        {
            passwordBox.Clear();
        }
    }
}
