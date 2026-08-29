using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using ServerDesk.App.Localization;
using ServerDesk.Application.Backups;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class BackupRestoreWindow : Window
{
    private readonly IBackupRestoreService _service;
    private readonly ILocalizationService _localization;
    private readonly ServerProfile _profile;
    private readonly bool _connected;
    private readonly ObservableCollection<BackupManifest> _history = [];
    private RestorePreview? _preview;
    private bool _busy;

    public BackupRestoreWindow(
        IBackupRestoreService service,
        ILocalizationService localization,
        ServerProfile profile,
        bool connected)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _connected = connected;
        InitializeComponent();
        BackupGrid.ItemsSource = _history;
        _localization.LanguageChanged += LocalizationOnLanguageChanged;
        Closed += (_, _) => _localization.LanguageChanged -= LocalizationOnLanguageChanged;
        ApplyLocalizedState();
    }

    private void LocalizationOnLanguageChanged() => ApplyLocalizedState();

    private void ApplyLocalizedState()
    {
        TitleText.Text = _localization.Format("Loc.BackupRestore.Title", _profile.Name);
        if (string.IsNullOrWhiteSpace(StatusText.Text))
        {
            StatusText.Text = _connected
                ? _localization.Get("Loc.BackupRestore.Ready")
                : _localization.Get("Loc.BackupRestore.Disconnected");
        }

        UpdateManifestText();
    }

    private void CreateEditorChanged(object sender, TextChangedEventArgs e)
    {
        // Backup creation always re-reads and verifies live source/destination state.
    }

    private async void CreateBackupOnClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var target = TargetPathTextBox.Text.Trim();
        var destination = DestinationTextBox.Text.Trim();
        if (MessageBox.Show(
                this,
                _localization.Format("Loc.BackupRestore.ConfirmCreate", target, destination),
                _localization.Get("Loc.BackupRestore.ConfirmCreateTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true);
        try
        {
            var result = await _service.CreateBackupAsync(
                _profile,
                new BackupCreateRequest(target, destination));
            StatusText.Text = result.Message;
            if (result.Manifest is { IsVerified: true } manifest)
            {
                _history.Insert(0, manifest);
                BackupGrid.SelectedItem = manifest;
            }
            else if (result.Error?.Code == ServerDesk.Domain.Errors.RemoteErrorCode.AmbiguousState)
            {
                MessageBox.Show(
                    this,
                    result.Message,
                    _localization.Get("Loc.BackupRestore.AmbiguousTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void BackupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        InvalidatePreview();
        UpdateManifestText();
    }

    private void UpdateManifestText()
    {
        ManifestText.Text = BackupGrid.SelectedItem is BackupManifest manifest
            ? _localization.Format(
                "Loc.BackupRestore.ManifestFormat",
                manifest.BackupId,
                manifest.TargetPath.Value,
                manifest.Sha256,
                manifest.Permissions.ToString())
            : _localization.Get("Loc.BackupRestore.NoManifestSelected");
    }

    private async void PreviewRestoreOnClick(object sender, RoutedEventArgs e)
    {
        if (_busy || BackupGrid.SelectedItem is not BackupManifest manifest)
        {
            return;
        }

        SetBusy(true);
        InvalidatePreview();
        try
        {
            var result = await _service.PreviewRestoreAsync(_profile, manifest);
            if (!result.IsSuccess || result.Preview is null)
            {
                StatusText.Text = result.Error?.Message ?? _localization.Get("Loc.BackupRestore.PreviewFailed");
                return;
            }

            _preview = result.Preview;
            PreviewTextBox.Text = _preview.Summary + Environment.NewLine + _preview.Impact.Message;
            RollbackText.Text = _preview.Impact.RollbackAvailable
                ? _localization.Get("Loc.BackupRestore.RollbackAvailable")
                : _localization.Get("Loc.BackupRestore.RollbackUnavailable");
            ExecuteRestoreButton.IsEnabled = true;
            StatusText.Text = _localization.Get("Loc.BackupRestore.PreviewReady");
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ExecuteRestoreOnClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _preview is null)
        {
            return;
        }

        var preview = _preview;
        if (MessageBox.Show(
                this,
                _localization.Format(
                    "Loc.BackupRestore.ConfirmRestore",
                    preview.Manifest.BackupId,
                    preview.Impact.ExactOverwriteTarget.Value,
                    preview.Impact.Message),
                _localization.Get("Loc.BackupRestore.ConfirmRestoreTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true);
        ExecuteRestoreButton.IsEnabled = false;
        try
        {
            var result = await _service.ExecuteRestoreAsync(_profile, preview);
            _preview = null;
            PreviewTextBox.Clear();
            RollbackText.Text = string.Empty;
            StatusText.Text = result.Message;
            if (result.AmbiguousState)
            {
                MessageBox.Show(
                    this,
                    result.Message,
                    _localization.Get("Loc.BackupRestore.AmbiguousTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void InvalidatePreview()
    {
        _preview = null;
        if (PreviewTextBox is not null)
        {
            PreviewTextBox.Clear();
            RollbackText.Text = string.Empty;
            ExecuteRestoreButton.IsEnabled = false;
        }
    }

    private void SetBusy(bool value)
    {
        _busy = value;
        CreateButton.IsEnabled = !value;
        BackupGrid.IsEnabled = !value;
        PreviewRestoreButton.IsEnabled = !value;
        TargetPathTextBox.IsEnabled = !value;
        DestinationTextBox.IsEnabled = !value;
        if (value)
        {
            ExecuteRestoreButton.IsEnabled = false;
        }
        else if (_preview is not null)
        {
            ExecuteRestoreButton.IsEnabled = true;
        }
    }
}
