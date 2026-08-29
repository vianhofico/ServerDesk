using System.Windows;
using System.Windows.Controls;
using ServerDesk.Application.Databases;

namespace ServerDesk.App;

public partial class DatabaseBackupWindow
{
    private IDatabaseRestoreService? _restoreService;

    internal void InitializeRestoreWorkflow(IDatabaseRestoreService restoreService)
    {
        _restoreService = restoreService ?? throw new ArgumentNullException(nameof(restoreService));
        UpdateRestoreButtonState();
    }

    private void HistorySelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateRestoreButtonState();

    private void UpdateRestoreButtonState()
    {
        if (RestoreButton is null)
        {
            return;
        }

        RestoreButton.IsEnabled =
            !_busy && _connected && _restoreService is not null && HistoryGrid.SelectedItem is BackupHistoryRow;
    }

    private void RestoreOnClick(object sender, RoutedEventArgs e)
    {
        if (_busy || !_connected || _restoreService is null || HistoryGrid.SelectedItem is not BackupHistoryRow row)
        {
            StatusText.Text = !_connected
                ? _localization.Get("Loc.DatabaseRestores.Disconnected")
                : _localization.Get("Loc.DatabaseRestores.Stale");
            return;
        }

        var matches = _lastHistory
            .Where(item =>
                item.IsVerified &&
                string.Equals(item.BackupPath.Value, row.Path, StringComparison.Ordinal) &&
                string.Equals(item.Verification.Sha256, row.Checksum, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        if (matches.Length != 1)
        {
            StatusText.Text = _localization.Get("Loc.DatabaseRestores.Stale");
            UpdateRestoreButtonState();
            return;
        }

        var window = new DatabaseRestoreWindow(
            _restoreService,
            _localization,
            _serverProfile,
            _databaseProfile,
            matches[0],
            _connected)
        {
            Owner = this,
        };
        window.ShowDialog();
        UpdateRestoreButtonState();
    }
}
