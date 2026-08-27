using System.Windows;
using ServerDesk.Application.HostTrust;

namespace ServerDesk.App.Presentation;

public sealed class WpfHostTrustPrompt : IHostTrustPrompt
{
    public ValueTask<HostTrustDecision> PromptAsync(
        HostTrustChallenge challenge,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        cancellationToken.ThrowIfCancellationRequested();

        var application = System.Windows.Application.Current
            ?? throw new InvalidOperationException("WPF application is not available.");

        HostTrustDecision ShowDialog()
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dialog = new HostTrustDialog(challenge);
            if (application.MainWindow is { IsVisible: true } owner)
            {
                dialog.Owner = owner;
            }

            dialog.ShowDialog();
            return dialog.Decision;
        }

        var decision = application.Dispatcher.CheckAccess()
            ? ShowDialog()
            : application.Dispatcher.Invoke(ShowDialog);
        return ValueTask.FromResult(decision);
    }
}
