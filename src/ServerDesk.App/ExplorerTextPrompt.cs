using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ServerDesk.App;

internal static class ExplorerTextPrompt
{
    public static string? Show(Window owner, string title, string label, string initialValue = "")
    {
        var textBox = new TextBox
        {
            Text = initialValue,
            MinWidth = 360,
            Margin = new Thickness(0, 8, 0, 14),
        };
        textBox.SelectAll();

        var okButton = new Button
        {
            Content = "OK",
            MinWidth = 84,
            IsDefault = true,
            Margin = new Thickness(8, 0, 0, 0),
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 84,
            IsCancel = true,
            Margin = new Thickness(8, 0, 0, 0),
        };

        var result = false;
        var window = new Window
        {
            Owner = owner,
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = owner.TryFindResource("WindowBackgroundBrush") as System.Windows.Media.Brush,
            Foreground = owner.TryFindResource("PrimaryTextBrush") as System.Windows.Media.Brush,
            Padding = new Thickness(20),
        };

        okButton.Click += (_, _) =>
        {
            result = true;
            window.DialogResult = true;
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(okButton);

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(textBox);
        panel.Children.Add(buttons);
        window.Content = panel;
        window.Loaded += (_, _) => Keyboard.Focus(textBox);

        _ = window.ShowDialog();
        return result ? textBox.Text : null;
    }
}
