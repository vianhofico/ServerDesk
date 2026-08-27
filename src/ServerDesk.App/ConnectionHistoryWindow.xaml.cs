using System.Globalization;
using System.Windows;
using ServerDesk.Application.History;

namespace ServerDesk.App;

public partial class ConnectionHistoryWindow : Window
{
    private readonly IConnectionHistoryRepository _historyRepository;

    public ConnectionHistoryWindow(IConnectionHistoryRepository historyRepository)
    {
        InitializeComponent();
        _historyRepository = historyRepository;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await RefreshAsync().ConfigureAwait(true);
    }

    private async void RefreshOnClick(object sender, RoutedEventArgs e)
    {
        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task RefreshAsync()
    {
        StatusText.Text = "Loading recent attempts…";
        try
        {
            var entries = await _historyRepository
                .ListRecentAsync(ConnectionHistoryPolicy.DefaultUiLimit)
                .ConfigureAwait(true);
            HistoryGrid.ItemsSource = entries.Select(entry => new HistoryRow(
                    entry.StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture),
                    entry.ProfileName,
                    entry.Endpoint,
                    entry.RouteSummary,
                    FormatOutcome(entry.Outcome),
                    FormatDuration(entry.Duration),
                    entry.FailureCode?.ToString() ?? "—"))
                .ToArray();
            StatusText.Text = entries.Count == 1
                ? "Showing 1 recent connection attempt."
                : $"Showing {entries.Count} recent connection attempts.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            HistoryGrid.ItemsSource = Array.Empty<HistoryRow>();
            StatusText.Text = $"Could not load connection history: {exception.Message}";
        }
    }

    private static string FormatOutcome(ConnectionAttemptOutcome outcome) =>
        outcome switch
        {
            ConnectionAttemptOutcome.Connected => "Connected",
            ConnectionAttemptOutcome.Cancelled => "Cancelled",
            ConnectionAttemptOutcome.AuthenticationFailed => "Authentication failed",
            ConnectionAttemptOutcome.HostTrustFailed => "Host trust failed",
            ConnectionAttemptOutcome.NetworkFailed => "Network failed",
            _ => "Failed",
        };

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalSeconds < 1
            ? $"{Math.Max(0, duration.TotalMilliseconds):0} ms"
            : $"{duration.TotalSeconds:0.0} s";

    private sealed record HistoryRow(
        string Started,
        string ProfileName,
        string Endpoint,
        string Route,
        string Outcome,
        string Duration,
        string FailureCode);
}
