using System.Windows;
using System.Windows.Controls;
using ServerDesk.App.Localization;
using ServerDesk.Application.Packages;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class PackageAdministrationWindow : Window
{
    private readonly IPackageManager _packageManager;
    private readonly ILocalizationService _localization;
    private readonly ServerProfile _profile;
    private readonly bool _connected;
    private PackageInventorySnapshot? _snapshot;
    private PackageMutationPreview? _preview;
    private bool _busy;

    public PackageAdministrationWindow(
        IPackageManager packageManager,
        ILocalizationService localization,
        ServerProfile profile,
        bool connected)
    {
        _packageManager = packageManager ?? throw new ArgumentNullException(nameof(packageManager));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _connected = connected;
        InitializeComponent();
        Loaded += OnLoaded;
        _localization.LanguageChanged += LocalizationOnLanguageChanged;
        Closed += (_, _) => _localization.LanguageChanged -= LocalizationOnLanguageChanged;
        PopulateOperations();
        ApplyLocalizedState();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void LocalizationOnLanguageChanged()
    {
        PopulateOperations();
        ApplyLocalizedState();
    }

    private void PopulateOperations()
    {
        var selected = OperationComboBox.SelectedValue is PackageMutationKind kind
            ? kind
            : PackageMutationKind.RefreshMetadata;
        OperationComboBox.ItemsSource = new[]
        {
            new Choice<PackageMutationKind>(PackageMutationKind.RefreshMetadata, _localization.Get("Loc.PackageAdmin.RefreshMetadata")),
            new Choice<PackageMutationKind>(PackageMutationKind.Install, _localization.Get("Loc.PackageAdmin.Install")),
            new Choice<PackageMutationKind>(PackageMutationKind.Upgrade, _localization.Get("Loc.PackageAdmin.Upgrade")),
            new Choice<PackageMutationKind>(PackageMutationKind.Remove, _localization.Get("Loc.PackageAdmin.Remove")),
        };
        OperationComboBox.SelectedValue = selected;
    }

    private void ApplyLocalizedState()
    {
        TitleText.Text = _localization.Format("Loc.PackageAdmin.Title", _profile.Name);
        if (_snapshot is not null)
        {
            StatusText.Text = _localization.Format(
                "Loc.PackageAdmin.StatusLoaded",
                _snapshot.ActiveManager?.ToString() ?? _localization.Get("Loc.PackageAdmin.None"),
                _snapshot.Packages.Count,
                _snapshot.Detail);
        }
        else
        {
            StatusText.Text = _connected
                ? _localization.Get("Loc.PackageAdmin.StatusReady")
                : _localization.Get("Loc.PackageAdmin.StatusDisconnected");
        }

        UpdateSelectionText();
    }

    private async void RefreshOnClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);
        InvalidatePreview();
        try
        {
            StatusText.Text = _localization.Get("Loc.PackageAdmin.StatusLoading");
            var result = await _packageManager.InspectAsync(_profile);
            if (!result.IsSuccess || result.Snapshot is null)
            {
                _snapshot = result.Snapshot;
                PackageGrid.ItemsSource = result.Snapshot?.Packages ?? [];
                ManagerText.Text = result.Snapshot?.ActiveManager?.ToString() ?? _localization.Get("Loc.PackageAdmin.None");
                StatusText.Text = result.Error?.Message ?? result.Snapshot?.Detail ?? _localization.Get("Loc.PackageAdmin.StatusFailed");
                return;
            }

            _snapshot = result.Snapshot;
            PackageGrid.ItemsSource = _snapshot.Packages;
            ManagerText.Text = _snapshot.ActiveManager?.ToString() ?? _localization.Get("Loc.PackageAdmin.None");
            ApplyLocalizedState();
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

    private void PackageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectionText();
        InvalidatePreview();
    }

    private void EditorChanged(object sender, SelectionChangedEventArgs e) => InvalidatePreview();

    private void UpdateSelectionText()
    {
        var names = PackageGrid.SelectedItems
            .OfType<PackageInfo>()
            .Select(item => item.Name)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        SelectedPackagesText.Text = names.Length == 0
            ? _localization.Get("Loc.PackageAdmin.NoPackagesSelected")
            : string.Join(", ", names);
    }

    private async void PreviewOnClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _snapshot?.ActiveManager is not { } manager ||
            OperationComboBox.SelectedValue is not PackageMutationKind operation)
        {
            return;
        }

        var names = PackageGrid.SelectedItems
            .OfType<PackageInfo>()
            .Select(item => item.Name)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        if (operation != PackageMutationKind.RefreshMetadata && names.Length == 0)
        {
            MessageBox.Show(
                this,
                _localization.Get("Loc.PackageAdmin.SelectPackageFirst"),
                _localization.Get("Loc.PackageAdmin.WindowTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        SetBusy(true);
        InvalidatePreview();
        try
        {
            var result = await _packageManager.PreviewAsync(
                _profile,
                new PackageMutationRequest(
                    operation,
                    manager,
                    operation == PackageMutationKind.RefreshMetadata ? [] : names));
            if (!result.IsSuccess || result.Preview is null)
            {
                StatusText.Text = result.Error?.Message ?? _localization.Get("Loc.PackageAdmin.PreviewFailed");
                return;
            }

            _preview = result.Preview;
            PreviewTextBox.Text = _preview.DisplayCommand;
            ImpactText.Text = _preview.ImpactHint.Message;
            ExecuteButton.IsEnabled = true;
            StatusText.Text = _localization.Get("Loc.PackageAdmin.PreviewReady");
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

    private async void ExecuteOnClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _preview is null)
        {
            return;
        }

        var preview = _preview;
        var confirmation = _localization.Format(
            "Loc.PackageAdmin.ConfirmBody",
            preview.DisplayCommand,
            preview.ImpactHint.Message);
        if (MessageBox.Show(
                this,
                confirmation,
                _localization.Get("Loc.PackageAdmin.ConfirmTitle"),
                MessageBoxButton.YesNo,
                preview.Risk == ServerDesk.Domain.Operations.OperationRisk.Destructive
                    ? MessageBoxImage.Warning
                    : MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true);
        ExecuteButton.IsEnabled = false;
        try
        {
            var result = await _packageManager.ExecuteAsync(_profile, preview);
            _preview = null;
            PreviewTextBox.Clear();
            ImpactText.Text = string.Empty;
            StatusText.Text = result.Message;
            if (result.VerifiedSnapshot is not null)
            {
                _snapshot = result.VerifiedSnapshot;
                PackageGrid.ItemsSource = _snapshot.Packages;
                ManagerText.Text = _snapshot.ActiveManager?.ToString() ?? _localization.Get("Loc.PackageAdmin.None");
            }
            else
            {
                await RefreshAsync();
            }

            if (result.AmbiguousState)
            {
                MessageBox.Show(
                    this,
                    result.Message,
                    _localization.Get("Loc.PackageAdmin.AmbiguousTitle"),
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
            ImpactText.Text = string.Empty;
            ExecuteButton.IsEnabled = false;
        }
    }

    private void SetBusy(bool value)
    {
        _busy = value;
        RefreshButton.IsEnabled = !value;
        PreviewButton.IsEnabled = !value;
        PackageGrid.IsEnabled = !value;
        OperationComboBox.IsEnabled = !value;
        if (value)
        {
            ExecuteButton.IsEnabled = false;
        }
        else if (_preview is not null)
        {
            ExecuteButton.IsEnabled = true;
        }
    }

    private sealed record Choice<T>(T Value, string Text);
}
