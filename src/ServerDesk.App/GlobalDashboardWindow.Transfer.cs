using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ServerDesk.Application.Profiles;

namespace ServerDesk.App;

public partial class GlobalDashboardWindow
{
    private IProfileMetadataTransferService? _profileMetadataTransferService;
    private Button? _exportSelectedButton;
    private Button? _importMetadataButton;

    internal void InitializeProfileTransfer(IProfileMetadataTransferService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (_profileMetadataTransferService is not null)
        {
            throw new InvalidOperationException("Profile metadata transfer is already initialized.");
        }

        _profileMetadataTransferService = service;
        if (RefreshSelectedButton.Parent is not Panel actions)
        {
            throw new InvalidOperationException("Global dashboard action panel was not found.");
        }

        _exportSelectedButton = CreateActionButton("Loc.Transfer.ExportSelected", ExportSelectedOnClick);
        _importMetadataButton = CreateActionButton("Loc.Transfer.ImportMetadata", ImportMetadataOnClick);
        var insertIndex = actions.Children.IndexOf(BulkMetadataButton) + 1;
        actions.Children.Insert(insertIndex, _exportSelectedButton);
        actions.Children.Insert(insertIndex + 1, _importMetadataButton);
        UpdateTransferButtons();
    }

    private static Button CreateActionButton(string resourceKey, RoutedEventHandler handler)
    {
        var button = new Button
        {
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(14, 7, 14, 7),
        };
        button.SetResourceReference(ContentControl.ContentProperty, resourceKey);
        button.Click += handler;
        return button;
    }

    private void UpdateTransferButtons()
    {
        if (_profileMetadataTransferService is null)
        {
            return;
        }

        var available = !_isRefreshing && !_isLoading;
        if (_exportSelectedButton is not null)
        {
            _exportSelectedButton.IsEnabled = available && ServerGrid.SelectedItems.Count >= 1;
        }

        if (_importMetadataButton is not null)
        {
            _importMetadataButton.IsEnabled = available;
        }
    }

    private async void ExportSelectedOnClick(object sender, RoutedEventArgs e)
    {
        if (_profileMetadataTransferService is null || _isRefreshing || _isLoading)
        {
            SetStatus("Loc.Transfer.Busy");
            return;
        }

        var profileIds = ServerGrid.SelectedItems
            .OfType<GlobalDashboardRowViewModel>()
            .Select(row => row.Profile.Id)
            .Distinct()
            .ToArray();
        if (profileIds.Length == 0)
        {
            SetStatus("Loc.Transfer.SelectExportTarget");
            UpdateTransferButtons();
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = _localization.Get("Loc.Transfer.ExportDialogTitle"),
            Filter = _localization.Get("Loc.Transfer.JsonFilter"),
            DefaultExt = ".json",
            AddExtension = true,
            FileName = "serverdesk-profile-metadata.json",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var json = await _profileMetadataTransferService
                .ExportAsync(profileIds)
                .ConfigureAwait(true);
            await File.WriteAllTextAsync(dialog.FileName, json).ConfigureAwait(true);
            SetStatus("Loc.Transfer.ExportComplete", profileIds.Length);
        }
        catch
        {
            SetStatus("Loc.Transfer.ExportFailed");
        }
    }

    private async void ImportMetadataOnClick(object sender, RoutedEventArgs e)
    {
        if (_profileMetadataTransferService is null || _isRefreshing || _isLoading)
        {
            SetStatus("Loc.Transfer.Busy");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = _localization.Get("Loc.Transfer.ImportDialogTitle"),
            Filter = _localization.Get("Loc.Transfer.JsonFilter"),
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        ProfileMetadataTransferDocument document;
        try
        {
            var file = new FileInfo(dialog.FileName);
            if (file.Length > ProfileMetadataTransferLimits.MaxDocumentBytes)
            {
                SetStatus("Loc.Transfer.ImportInvalid");
                return;
            }

            var json = await File.ReadAllTextAsync(dialog.FileName).ConfigureAwait(true);
            document = _profileMetadataTransferService.Parse(json);
        }
        catch
        {
            SetStatus("Loc.Transfer.ImportInvalid");
            return;
        }

        var window = new ProfileMetadataImportWindow(
            document,
            _profileMetadataTransferService,
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
            UpdateTransferButtons();
        }
    }
}
