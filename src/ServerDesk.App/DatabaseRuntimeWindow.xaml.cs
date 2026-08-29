using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using ServerDesk.App.Localization;
using ServerDesk.Application.Databases;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class DatabaseRuntimeWindow : Window
{
    private readonly IDatabaseRuntimeService _service;
    private readonly ILocalizationService _localization;
    private readonly ServerProfile _profile;
    private readonly bool _connected;
    private readonly ObservableCollection<DatabaseEngineObservation> _engines = [];
    private CancellationTokenSource? _refreshCancellation;
    private bool _busy;

    public DatabaseRuntimeWindow(
        IDatabaseRuntimeService service,
        ILocalizationService localization,
        ServerProfile profile,
        bool connected)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _connected = connected;
        InitializeComponent();
        EngineGrid.ItemsSource = _engines;
        _localization.LanguageChanged += LocalizationOnLanguageChanged;
        Closed += OnClosed;
        ApplyLocalizedState();
        Loaded += async (_, _) => await RefreshAsync().ConfigureAwait(true);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _localization.LanguageChanged -= LocalizationOnLanguageChanged;
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
    }

    private void LocalizationOnLanguageChanged() => ApplyLocalizedState();

    private void ApplyLocalizedState()
    {
        TitleText.Text = _localization.Format("Loc.DatabaseRuntime.Title", _profile.Name);
        if (!_busy && _engines.Count == 0)
        {
            StatusText.Text = _connected
                ? _localization.Get("Loc.DatabaseRuntime.Ready")
                : _localization.Get("Loc.DatabaseRuntime.Disconnected");
        }

        UpdateSummary();
        UpdateSelectionDetails();
    }

    private async void RefreshOnClick(object sender, RoutedEventArgs e) =>
        await RefreshAsync().ConfigureAwait(true);

    private void CancelOnClick(object sender, RoutedEventArgs e) =>
        _refreshCancellation?.Cancel();

    private async Task RefreshAsync()
    {
        if (_busy)
        {
            return;
        }

        _refreshCancellation?.Dispose();
        _refreshCancellation = new CancellationTokenSource();
        SetBusy(true);
        StatusText.Text = _localization.Get("Loc.DatabaseRuntime.Loading");
        try
        {
            var result = await _service.InspectAsync(_profile, _refreshCancellation.Token).ConfigureAwait(true);
            _engines.Clear();
            if (!result.IsSuccess || result.Snapshot is null)
            {
                StatusText.Text = result.Error?.Message ?? _localization.Get("Loc.DatabaseRuntime.LoadFailed");
                SummaryText.Text = string.Empty;
                return;
            }

            foreach (var engine in result.Snapshot.Engines)
            {
                _engines.Add(engine);
            }

            StatusText.Text = result.Snapshot.HasSupportedEngine
                ? _localization.Get("Loc.DatabaseRuntime.Loaded")
                : _localization.Get("Loc.DatabaseRuntime.NoEngines");
            EngineGrid.SelectedItem = _engines.FirstOrDefault(engine => engine.IsInstalled) ?? _engines.FirstOrDefault();
            UpdateSummary();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = _localization.Get("Loc.DatabaseRuntime.Cancelled");
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

    private void EngineSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateSelectionDetails();

    private void UpdateSummary()
    {
        if (_engines.Count == 0)
        {
            SummaryText.Text = string.Empty;
            return;
        }

        var installed = _engines.Count(engine => engine.IsInstalled);
        var active = _engines.Count(engine => engine.IsActive);
        SummaryText.Text = _localization.Format("Loc.DatabaseRuntime.Summary", installed, active);
    }

    private void UpdateSelectionDetails()
    {
        if (EngineGrid?.SelectedItem is not DatabaseEngineObservation engine)
        {
            SelectedTitleText.Text = _localization.Get("Loc.DatabaseRuntime.NoSelection");
            ExecutableText.Text = string.Empty;
            VersionText.Text = string.Empty;
            ServiceText.Text = string.Empty;
            JournalText.Text = string.Empty;
            DetailText.Text = string.Empty;
            return;
        }

        SelectedTitleText.Text = $"{engine.Engine} — {engine.Status}";
        ExecutableText.Text = _localization.Format("Loc.DatabaseRuntime.ExecutableFormat", engine.Executable);
        VersionText.Text = _localization.Format(
            "Loc.DatabaseRuntime.VersionFormat",
            engine.Version ?? _localization.Get("Loc.DatabaseRuntime.UnknownValue"));
        ServiceText.Text = _localization.Format(
            "Loc.DatabaseRuntime.ServiceFormat",
            engine.ServiceUnit ?? _localization.Get("Loc.DatabaseRuntime.UnknownValue"),
            engine.ActiveState ?? _localization.Get("Loc.DatabaseRuntime.UnknownValue"),
            engine.SubState ?? _localization.Get("Loc.DatabaseRuntime.UnknownValue"));
        JournalText.Text = engine.JournalUnit is null
            ? _localization.Get("Loc.DatabaseRuntime.JournalUnavailable")
            : _localization.Format("Loc.DatabaseRuntime.JournalFormat", engine.JournalUnit);
        DetailText.Text = engine.Detail;
    }

    private void SetBusy(bool value)
    {
        _busy = value;
        RefreshButton.IsEnabled = !value;
        CancelButton.IsEnabled = value;
        EngineGrid.IsEnabled = !value;
    }
}
