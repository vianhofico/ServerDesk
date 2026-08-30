using System.Globalization;
using System.Windows;
using ServerDesk.App.Localization;
using ServerDesk.Application.Databases;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class DatabaseRestoreWindow : Window
{
    private readonly IDatabaseRestoreService _restoreService;
    private readonly ILocalizationService _localization;
    private readonly ServerProfile _serverProfile;
    private readonly DatabaseConnectionProfile _databaseProfile;
    private readonly DatabaseBackupManifest _manifest;
    private readonly bool _connected;
    private DatabaseRestorePreview? _preview;
    private bool _busy;

    public DatabaseRestoreWindow(
        IDatabaseRestoreService restoreService,
        ILocalizationService localization,
        ServerProfile serverProfile,
        DatabaseConnectionProfile databaseProfile,
        DatabaseBackupManifest manifest,
        bool connected)
    {
        _restoreService = restoreService ?? throw new ArgumentNullException(nameof(restoreService));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _serverProfile = serverProfile ?? throw new ArgumentNullException(nameof(serverProfile));
        _databaseProfile = databaseProfile ?? throw new ArgumentNullException(nameof(databaseProfile));
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _connected = connected;

        InitializeComponent();
        _localization.LanguageChanged += LocalizationOnLanguageChanged;
        Closed += OnClosed;
        ApplyStaticState();
        ClearPreview();
    }

    private void OnClosed(object? sender, EventArgs e) =>
        _localization.LanguageChanged -= LocalizationOnLanguageChanged;

    private void LocalizationOnLanguageChanged()
    {
        ApplyStaticState();
        if (_preview is not null)
        {
            RenderPreview(_preview);
        }
    }

    private void ApplyStaticState()
    {
        TitleText.Text = _localization.Format("Loc.DatabaseRestores.Title", _databaseProfile.Name);
        ServerText.Text = ServerEndpoint();
        EngineText.Text = _databaseProfile.Engine.ToString();
        TargetText.Text = _manifest.DatabaseName;
        BackupIdText.Text = _manifest.BackupId.ToString("D");
        BackupPathText.Text = _manifest.BackupPath.Value;
        BackupShaText.Text = _manifest.Verification.Sha256;
        BackupSizeText.Text = FormatBytes(_manifest.Verification.SizeBytes);
        BackupToolText.Text = $"{_manifest.ToolName} · {_manifest.ToolVersion}";
        PreviewButton.IsEnabled = !_busy && _connected;
        ExecuteButton.IsEnabled = !_busy && _connected && _preview is not null;
        if (!_connected && string.IsNullOrWhiteSpace(StatusText.Text))
        {
            StatusText.Text = _localization.Get("Loc.DatabaseRestores.Disconnected");
        }
    }

    private async void PreviewOnClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (!_connected)
        {
            StatusText.Text = _localization.Get("Loc.DatabaseRestores.Disconnected");
            return;
        }

        ClearPreview();
        SetBusy(true);
        StatusText.Text = _localization.Get("Loc.DatabaseRestores.Previewing");
        try
        {
            var result = await _restoreService.PreviewAsync(
                    _serverProfile,
                    new DatabaseRestoreRequest(
                        _databaseProfile.Id,
                        _manifest.BackupId,
                        _manifest.DatabaseName),
                    CancellationToken.None)
                .ConfigureAwait(true);
            if (!result.IsSuccess || result.Preview is null)
            {
                StatusText.Text = result.Unsupported
                    ? _localization.Get("Loc.DatabaseRestores.Unsupported")
                    : _localization.Get(FailureLocalizationKey(result.Error?.Code));
                return;
            }

            _preview = result.Preview;
            RenderPreview(_preview);
            StatusText.Text = _localization.Get("Loc.DatabaseRestores.PreviewReady");
        }
        catch
        {
            ClearPreview();
            StatusText.Text = _localization.Get("Loc.DatabaseRestores.FailureUnexpected");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ExecuteOnClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _preview is null)
        {
            return;
        }

        var preview = _preview;
        var confirmation = MessageBox.Show(
            _localization.Format(
                "Loc.DatabaseRestores.Confirm",
                preview.ServerEndpoint,
                preview.Request.TargetDatabase,
                preview.Request.BackupId,
                preview.BackupPath,
                $"{preview.RestoreTool} · {preview.RestoreToolVersion}",
                preview.DataLossWarning),
            _localization.Get("Loc.DatabaseRestores.ConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true);
        StatusText.Text = _localization.Get("Loc.DatabaseRestores.Executing");
        try
        {
            var result = await _restoreService.ExecuteAsync(
                    _serverProfile,
                    preview,
                    CancellationToken.None)
                .ConfigureAwait(true);

            _preview = null;
            ExecuteButton.IsEnabled = false;
            if (result.IsSuccess)
            {
                StatusText.Text = _localization.Get("Loc.DatabaseRestores.Success");
                if (result.VerifiedTarget is not null)
                {
                    LiveIdentityText.Text = $"{result.VerifiedTarget.DatabaseName} · {result.VerifiedTarget.ConnectionIdentity} · {result.VerifiedTarget.ServerVersion}";
                    LiveObjectsText.Text = result.VerifiedTarget.UserObjectCount.ToString(CultureInfo.CurrentCulture);
                }

                return;
            }

            StatusText.Text = result.AmbiguousState
                ? _localization.Get("Loc.DatabaseRestores.Ambiguous")
                : _localization.Get(FailureLocalizationKey(result.Error?.Code));
        }
        catch
        {
            _preview = null;
            ExecuteButton.IsEnabled = false;
            StatusText.Text = _localization.Get("Loc.DatabaseRestores.FailureUnexpected");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RenderPreview(DatabaseRestorePreview preview)
    {
        RestoreToolText.Text = $"{preview.RestoreTool} · {preview.RestoreToolVersion}";
        LiveIdentityText.Text = $"{preview.TargetBefore.DatabaseName} · {preview.TargetBefore.ConnectionIdentity} · {preview.TargetBefore.ServerVersion}";
        LiveObjectsText.Text = preview.TargetBefore.UserObjectCount.ToString(CultureInfo.CurrentCulture);
        CommandText.Text = preview.DisplayCommand;
        WarningText.Text = preview.DataLossWarning;
    }

    private void ClearPreview()
    {
        _preview = null;
        if (ExecuteButton is not null)
        {
            ExecuteButton.IsEnabled = false;
        }

        if (RestoreToolText is not null)
        {
            RestoreToolText.Text = string.Empty;
            LiveIdentityText.Text = string.Empty;
            LiveObjectsText.Text = string.Empty;
            CommandText.Text = string.Empty;
            WarningText.Text = string.Empty;
        }
    }

    private void SetBusy(bool value)
    {
        _busy = value;
        PreviewButton.IsEnabled = !value && _connected;
        ExecuteButton.IsEnabled = !value && _connected && _preview is not null;
    }

    private static string FailureLocalizationKey(RemoteErrorCode? code) => code switch
    {
        RemoteErrorCode.PathConflict or RemoteErrorCode.InvalidEndpoint or RemoteErrorCode.PathNotFound => "Loc.DatabaseRestores.Stale",
        RemoteErrorCode.AuthenticationFailed => "Loc.DatabaseRestores.FailureAuth",
        RemoteErrorCode.PermissionDenied or RemoteErrorCode.SudoRequired => "Loc.DatabaseRestores.FailurePermission",
        RemoteErrorCode.ConnectionFailed or RemoteErrorCode.NetworkInterrupted or RemoteErrorCode.CommandTimeout => "Loc.DatabaseRestores.FailureNetwork",
        RemoteErrorCode.CapabilityUnavailable => "Loc.DatabaseRestores.FailureCapability",
        RemoteErrorCode.AmbiguousState or RemoteErrorCode.OperationCancelled => "Loc.DatabaseRestores.Ambiguous",
        _ => "Loc.DatabaseRestores.FailureUnexpected",
    };

    private string ServerEndpoint() =>
        $"{_serverProfile.Username}@{_serverProfile.Host}:{_serverProfile.Port.ToString(CultureInfo.InvariantCulture)}";

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

    private void CloseOnClick(object sender, RoutedEventArgs e) => Close();
}
