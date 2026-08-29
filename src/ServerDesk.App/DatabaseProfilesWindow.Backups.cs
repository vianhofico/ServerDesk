using System.Windows;
using System.Windows.Controls;
using ServerDesk.Application.Databases;

namespace ServerDesk.App;

public partial class DatabaseProfilesWindow
{
    private IDatabaseBackupService? _backupService;
    private IDatabaseRestoreService? _restoreService;

    internal void InitializeBackupWorkflow(
        IDatabaseBackupService backupService,
        IDatabaseRestoreService restoreService)
    {
        _backupService = backupService ?? throw new ArgumentNullException(nameof(backupService));
        _restoreService = restoreService ?? throw new ArgumentNullException(nameof(restoreService));
        BackupsButton.IsEnabled = _connected && !_busy && _selectedProfile is not null;
        ProfilesGrid.SelectionChanged += BackupSelectionChanged;
    }

    private void BackupSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        BackupsButton.IsEnabled = _connected && !_busy && _selectedProfile is not null;

    private void BackupsOnClick(object sender, RoutedEventArgs e)
    {
        if (!EnsureConnectedSelection())
        {
            return;
        }

        if (_backupService is null || _restoreService is null)
        {
            StatusText.Text = _localization.Get("Loc.DatabaseBackups.FailureUnexpected");
            return;
        }

        var window = new DatabaseBackupWindow(
            _backupService,
            _localization,
            _serverProfile,
            _selectedProfile!,
            _connected)
        {
            Owner = this,
        };
        window.InitializeRestoreWorkflow(_restoreService);
        window.ShowDialog();
    }
}
