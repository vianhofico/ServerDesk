using System.Windows;
using ServerDesk.Application.Dashboard;
using ServerDesk.Application.Profiles;

namespace ServerDesk.App;

public partial class GlobalDashboardWindow
{
    private void UpdateSelectionButtons()
    {
        var selectedCount = ServerGrid.SelectedItems.Count;
        var available = !_isRefreshing && !_isLoading;
        RefreshSelectedButton.IsEnabled = available && selectedCount >= 1;
        BulkMetadataButton.IsEnabled = available && selectedCount >= 1;
        CompareButton.IsEnabled = available && selectedCount >= 2;
    }

    private async void RefreshSelectedOnClick(object sender, RoutedEventArgs e)
    {
        if (_isRefreshing || _isLoading)
        {
            SetStatus("Loc.Bulk.Busy");
            return;
        }

        var selectedRows = ServerGrid.SelectedItems
            .OfType<GlobalDashboardRowViewModel>()
            .ToArray();
        if (selectedRows.Length == 0)
        {
            SetStatus("Loc.Bulk.SelectTarget");
            UpdateSelectionButtons();
            return;
        }

        await RefreshSelectedAsync(selectedRows).ConfigureAwait(true);
    }

    private async Task RefreshSelectedAsync(IReadOnlyList<GlobalDashboardRowViewModel> selectedRows)
    {
        _refreshCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _refreshCancellation = cancellation;
        _isRefreshing = true;
        SyncConnectionStates();
        UpdateButtons();
        UpdateSelectionButtons();

        var targets = selectedRows
            .Select(row => new MultiServerDashboardTarget(row.Profile, row.IsConnected))
            .ToArray();
        SetStatus(
            "Loc.Bulk.RefreshingSelected",
            targets.Count(target => target.IsConnected),
            targets.Length);

        try
        {
            await _refreshService.RefreshAsync(
                targets,
                update => PublishUpdateAsync(update, cancellation.Token),
                cancellation.Token).ConfigureAwait(true);

            if (!cancellation.IsCancellationRequested && ReferenceEquals(_refreshCancellation, cancellation))
            {
                var completed = selectedRows.Count(row => row.HasAvailableSnapshot);
                var failed = selectedRows.Count(row => row.HealthState == GlobalDashboardHealthState.Failed);
                var disconnected = selectedRows.Count(row => row.HealthState == GlobalDashboardHealthState.Disconnected);
                SetStatus(
                    "Loc.Bulk.RefreshSelectedComplete",
                    completed,
                    failed,
                    disconnected,
                    selectedRows.Count);
            }
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_refreshCancellation, cancellation))
            {
                SetStatus("Loc.GlobalDashboard.RefreshCancelled");
            }
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, cancellation))
            {
                _refreshCancellation = null;
                _isRefreshing = false;
                UpdateButtons();
                UpdateSelectionButtons();
            }

            cancellation.Dispose();
        }
    }

    private async void BulkMetadataOnClick(object sender, RoutedEventArgs e)
    {
        if (_isRefreshing || _isLoading)
        {
            SetStatus("Loc.Bulk.Busy");
            return;
        }

        var selectedRows = ServerGrid.SelectedItems
            .OfType<GlobalDashboardRowViewModel>()
            .ToArray();
        if (selectedRows.Length == 0)
        {
            SetStatus("Loc.Bulk.SelectTarget");
            UpdateSelectionButtons();
            return;
        }

        var targets = selectedRows
            .Select(row => new BulkProfileMetadataTarget(row.Profile.Id, row.Name, row.Endpoint))
            .ToArray();
        var window = new BulkMetadataMutationWindow(
            targets,
            new BulkProfileMetadataMutationService(_organizationService),
            _localization)
        {
            Owner = this,
        };
        _ = window.ShowDialog();

        if (window.HasChanges)
        {
            await LoadProfilesAsync().ConfigureAwait(true);
        }
        else
        {
            UpdateSelectionButtons();
        }
    }
}
