using System.Windows;
using ServerDesk.Application.HostTrust;

namespace ServerDesk.App;

public partial class HostTrustDialog : Window
{
    public HostTrustDialog(HostTrustChallenge challenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        InitializeComponent();

        EndpointText.Text = challenge.Observation.EndpointDisplay;
        AlgorithmText.Text = challenge.Observation.KeyAlgorithm;
        FingerprintText.Text = challenge.Observation.Fingerprint.Value;

        if (challenge.Kind == HostTrustChallengeKind.Changed)
        {
            HeadingText.Text = "SSH host key changed";
            ExplanationText.Text =
                "ServerDesk has a different identity saved for this endpoint. The current connection is blocked.";
            ChangedKeyWarning.Visibility = Visibility.Visible;
            UnknownHostNotice.Visibility = Visibility.Collapsed;
            TrustOnceButton.Visibility = Visibility.Collapsed;
            TrustAndSaveButton.Visibility = Visibility.Collapsed;
            ForgetKnownKeyButton.Visibility = Visibility.Visible;
            KnownKeysList.ItemsSource = challenge.KnownHosts.Select(record =>
                $"{record.KeyAlgorithm}  {record.Fingerprint.Value}").ToArray();
            return;
        }

        HeadingText.Text = "Unknown SSH host";
        ExplanationText.Text =
            "This server has not been trusted by ServerDesk before. Confirm its identity before continuing.";
    }

    public HostTrustDecision Decision { get; private set; } = HostTrustDecision.Cancel;

    private void TrustOnceButtonOnClick(object sender, RoutedEventArgs e)
    {
        Decision = HostTrustDecision.TrustOnce;
        DialogResult = true;
    }

    private void TrustAndSaveButtonOnClick(object sender, RoutedEventArgs e)
    {
        Decision = HostTrustDecision.TrustAndSave;
        DialogResult = true;
    }

    private void ForgetKnownKeyButtonOnClick(object sender, RoutedEventArgs e)
    {
        Decision = HostTrustDecision.ForgetKnownKey;
        DialogResult = true;
    }

    private void CancelButtonOnClick(object sender, RoutedEventArgs e)
    {
        Decision = HostTrustDecision.Cancel;
        DialogResult = false;
    }
}
