using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using ServerDesk.App.Localization;
using ServerDesk.Application.Databases;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class DatabaseBackupWindow : Window
{
    private readonly IDatabaseBackupService _backupService;
    private readonly ILocalizationService _localization;
    private readonly ServerProfile _serverProfile;
    private readonly DatabaseConnectionProfile _databaseProfile;
    private readonly bool _connected;
    private readonly ObservableCollection<BackupHistoryRow> _history = [];
    private IReadOnlyList<DatabaseBackupManifest> _lastHistory = [];
    private bool _busy;

    public DatabaseBackupWindow(
        IDatabaseBackupService backupService,
        ILocalizationService localization,
        ServerProfile serverProfile,
        DatabaseConnectionProfile databaseProfile,
        bool connected)
    {
        _backupService = backupService ?? throw new ArgumentNullException(nameof(backupService));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _serverProfile = serverProfile ?? throw new ArgumentNullException(nameof(serverProfile));
        _databaseProfile = databaseProfile ?? throw new ArgumentNullException(nameof(databaseProfile));
        _connected = connected;

        InitializeComponent();
        HistoryGrid.ItemsSource = _history;
        DatabaseNameBox.Text = databaseProfile.DatabaseName ?? string.Empty;
        DestinationBox.Text = "/var/backups/serverdesk";
        _localization.LanguageChanged += LocalizationOnLanguageChanged;
        Closed += OnClosed;
        Loaded += async (_, _) => await RefreshHistorySafeAsync().ConfigureAwait(true);
        ApplyLocalizedState();
    }

    private void OnClosed(object? sender, EventArgs e) =>
        _localization.LanguageChanged -= LocalizationOnLanguageChanged;

    private void LocalizationOnLanguageChanged()
    {
        ApplyLocalizedState();
        ApplyHistoryRows(_lastHistory);
    }

    private void ApplyLocalizedState()
    {
        TitleText.Text = _localization.Format("Loc.DatabaseBackups.Title", _databaseProfile.Name);
        ProfileSummaryText.Text = _localization.Format(
            "Loc.DatabaseBackups.ProfileSummary",
            _databaseProfile.Name,
            _databaseProfile.Engine,
            _databaseProfile.RemoteHost,
            _databaseProfile.RemotePort);
        CreateButton.IsEnabled = !_busy && _connected;
        RefreshButton.IsEnabled = !_busy;
        if (!_connected && string.IsNullOrWhiteSpace(StatusText.Text))
        {
            StatusText.Text = _localization.Get("Loc.DatabaseBackups.Disconnected");
        }
    }

    private async void CreateOnClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (!_connected)
        {
            StatusText.Text = _localization.Get("Loc.DatabaseBackups.Disconnected");
            return;
        }

        var databaseName = DatabaseNameBox.Text;
        var destination = DestinationBox.Text;
        if (string.IsNullOrWhiteSpace(databaseName) || string.IsNullOrWhiteSpace(destination))
        {
            StatusText.Text = _localization.Get("Loc.DatabaseBackups.InvalidInput");
            return;
        }

        SetBusy(true);
        StatusText.Text = _localization.Get("Loc.DatabaseBackups.Creating");
        try
        {
            var result = await _backupService.CreateAsync(
                    _serverProfile,
                    new DatabaseBackupRequest(_databaseProfile.Id, databaseName, destination),
                    CancellationToken.None)
                .ConfigureAwait(true);

            if (result.IsSuccess && result.Manifest is not null)
            {
                await RefreshHistorySafeAsync().ConfigureAwait(true);
                StatusText.Text = _localization.Format(
                    result.HistoryPersisted
                        ? "Loc.DatabaseBackups.Success"
                        : "Loc.DatabaseBackups.SuccessHistoryFailed",
                    result.Manifest.BackupPath.Value);
                return;
            }

            StatusText.Text = result.AmbiguousState
                ? _localization.Get("Loc.DatabaseBackups.Ambiguous")
                : result.Unsupported
                    ? _localization.Get("Loc.DatabaseBackups.Unsupported")
                    : _localization.Get(FailureLocalizationKey(result.Error?.Code));
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = _localization.Get("Loc.DatabaseBackups.Ambiguous");
        }
        catch
        {
            StatusText.Text = _localization.Get("Loc.DatabaseBackups.FailureUnexpected");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void RefreshOnClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);
        try
        {
            await RefreshHistorySafeAsync().ConfigureAwait(true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void CloseOnClick(object sender, RoutedEventArgs e) => Close();

    private async Task RefreshHistorySafeAsync()
    {
        StatusText.Text = _localization.Get("Loc.DatabaseBackups.Loading");
        try
        {
            var history = await _backupService.ListHistoryAsync(_serverProfile.Id, CancellationToken.None)
                .ConfigureAwait(true);
            _lastHistory = history
                .Where(item => item.DatabaseProfileId == _databaseProfile.Id && item.IsVerified)
                .OrderByDescending(item => item.CreatedAtUtc)
                .ToArray();
            ApplyHistoryRows(_lastHistory);
            StatusText.Text = _history.Count == 0
                ? _localization.Get("Loc.DatabaseBackups.HistoryEmpty")
                : string.Empty;
        }
        catch
        {
            _lastHistory = [];
            _history.Clear();
            StatusText.Text = _localization.Get("Loc.DatabaseBackups.HistoryFailure");
        }
    }

    private void ApplyHistoryRows(IEnumerable<DatabaseBackupManifest> history)
    {
        _history.Clear();
        foreach (var item in history)
        {
            _history.Add(new BackupHistoryRow(
                item.CreatedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                item.DatabaseName,
                item.BackupPath.Value,
                item.Format.ToString(),
                $"{item.ToolName} · {item.ToolVersion}",
                FormatBytes(item.Verification.SizeBytes),
                item.Verification.Sha256,
                item.Verification.VerifiedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)));
        }
    }

    private void SetBusy(bool value)
    {
        _busy = value;
        CreateButton.IsEnabled = !value && _connected;
        RefreshButton.IsEnabled = !value;
        DatabaseNameBox.IsEnabled = !value;
        DestinationBox.IsEnabled = !value;
    }

    private static string FailureLocalizationKey(RemoteErrorCode? code) => code switch
    {
        RemoteErrorCode.AuthenticationFailed => "Loc.DatabaseBackups.FailureAuth",
        RemoteErrorCode.PermissionDenied or RemoteErrorCode.SudoRequired => "Loc.DatabaseBackups.FailurePermission",
        RemoteErrorCode.ConnectionFailed or RemoteErrorCode.NetworkInterrupted => "Loc.DatabaseBackups.FailureNetwork",
        RemoteErrorCode.InvalidEndpoint or RemoteErrorCode.PathNotFound or RemoteErrorCode.PathConflict => "Loc.DatabaseBackups.FailurePath",
        RemoteErrorCode.CapabilityUnavailable => "Loc.DatabaseBackups.FailureCapability",
        RemoteErrorCode.CommandTimeout => "Loc.DatabaseBackups.FailureTimeout",
        RemoteErrorCode.AmbiguousState or RemoteErrorCode.OperationCancelled => "Loc.DatabaseBackups.Ambiguous",
        _ => "Loc.DatabaseBackups.FailureUnexpected",
    };

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var amount = (double)value;
        var unit = 0;
        while (amount >= 1024 && unit < units.Length - 1)
        {
            amount /= 1024;
            unit++;
        }

        return string.Format(CultureInfo.CurrentCulture, "{0:0.##} {1}", amount, units[unit]);
    }

    private sealed record BackupHistoryRow(
        string Created,
        string Database,
        string Path,
        string Format,
        string Tool,
        string Size,
        string Checksum,
        string Verified);
}
