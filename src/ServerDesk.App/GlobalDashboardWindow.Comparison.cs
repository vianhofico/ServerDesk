using System.Windows;
using System.Windows.Controls;
using ServerDesk.Application.Dashboard;

namespace ServerDesk.App;

public partial class GlobalDashboardWindow
{
    private readonly IMultiServerComparisonService _comparisonService = new MultiServerComparisonService();

    private void ServerGridOnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CompareButton.IsEnabled = !_isRefreshing && !_isLoading && ServerGrid.SelectedItems.Count >= 2;
    }

    private void CompareSelectedOnClick(object sender, RoutedEventArgs e)
    {
        if (_isRefreshing || _isLoading)
        {
            SetStatus("Loc.GlobalDashboard.CompareBusy");
            return;
        }

        var selectedRows = ServerGrid.SelectedItems
            .OfType<GlobalDashboardRowViewModel>()
            .ToArray();
        if (selectedRows.Length < 2)
        {
            CompareButton.IsEnabled = false;
            SetStatus("Loc.GlobalDashboard.CompareRequiresTwo");
            return;
        }

        var inputs = selectedRows
            .Select(row => new MultiServerComparisonInput(
                row.Profile,
                row.HasAvailableSnapshot &&
                _refreshService.TryGetCachedSnapshot(row.Profile.Id, out var snapshot)
                    ? snapshot
                    : null))
            .ToArray();
        var comparison = _comparisonService.Compare(inputs);
        var window = new ServerComparisonWindow(comparison, _localization)
        {
            Owner = this,
        };
        window.Show();
        SetStatus("Loc.GlobalDashboard.CompareOpened", selectedRows.Length);
    }
}
