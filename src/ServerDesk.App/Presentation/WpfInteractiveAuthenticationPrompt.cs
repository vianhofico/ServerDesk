using ServerDesk.Application.Sessions;

namespace ServerDesk.App.Presentation;

public sealed class WpfInteractiveAuthenticationPrompt : IInteractiveAuthenticationPrompt
{
    public ValueTask<IReadOnlyList<string>?> PromptAsync(
        InteractiveAuthenticationChallenge challenge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        cancellationToken.ThrowIfCancellationRequested();

        var application = System.Windows.Application.Current
            ?? throw new InvalidOperationException("WPF application is not available.");

        IReadOnlyList<string>? ShowDialog()
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dialog = new InteractiveAuthenticationDialog(challenge);
            if (application.MainWindow is { IsVisible: true } owner)
            {
                dialog.Owner = owner;
            }

            dialog.ShowDialog();
            return dialog.Responses;
        }

        var responses = application.Dispatcher.CheckAccess()
            ? ShowDialog()
            : application.Dispatcher.Invoke(ShowDialog);
        return ValueTask.FromResult(responses);
    }
}
