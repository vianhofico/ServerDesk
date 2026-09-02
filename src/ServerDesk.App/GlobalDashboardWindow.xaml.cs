using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using ServerDesk.App.Localization;
using ServerDesk.App.Presentation;
using ServerDesk.Application.Dashboard;
using ServerDesk.Application.Profiles;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public sealed record GlobalDashboardTarget(ServerProfile Profile, RemoteSessionState ConnectionState);

public partial class GlobalDashboardWindow : Window
{
    private readonly IMultiServerDashboardRefreshService _refreshService;
    private readonly IServerProfileOrganizationService _organizationService;
    private readonly ILocalizationService _localization;
    private readonly Func<IReadOnlyList<GlobalDashboardTarget>> _targetsProvider;
    private readonly Dictionary<Guid, GlobalDashboardRowViewModel> _rowCache = [];
    private CancellationTokenSource? _profileLoadCancellation;
    private CancellationTokenSource? _refreshCancellation;
    private string _statusKey = "Loc.GlobalDashboard.Ready";
    private object?[] _statusArguments = [];
    private bool _isRefreshing;
    private bool _isLoading;

    public GlobalDashboardWindow(
        IMultiServerDashboardRefreshService refreshService,
        IServerProfileOrganizationService organizationService,
        ILocalizationService localization,
        Func<IReadOnlyList<GlobalDashboardTarget>> targetsProvider)
    {
        InitializeComponent();
        _refreshService = refreshService ?? throw new ArgumentNullException(nameof(refreshService));
        _organizationService = organizationService ?? throw new ArgumentNullException(nameof(organizationService));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _targetsProvider = targetsProvider ?? throw new ArgumentNullException(nameof(targetsProvider));
        Rows = [];
        DataContext = this;
        Loaded += GlobalDashboardWindowOnLoaded;
        Closed += GlobalDashboardWindowOnClosed;
        _localization.LanguageChanged += LocalizationOnLanguageChanged;
        RenderStatus();
        UpdateButtons();
    }

    public ObservableCollection<GlobalDashboardRowViewModel> Rows { get; }

    private async void GlobalDashboardWindowOnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= GlobalDashboardWindowOnLoaded;
        await LoadProfilesAsync().ConfigureAwait(true);
    }

    private void GlobalDashboardWindowOnClosed(object? sender, EventArgs e)
    {
        Closed -= GlobalDashboardWindowOnClosed;
        _localization.LanguageChanged -= LocalizationOnLanguageChanged;
        _profileLoadCancellation?.Cancel();
        _refreshCancellation?.Cancel();
        _profileLoadCancellation?.Dispose();
        _refreshCancellation?.Dispose();
        _profileLoadCancellation = null;
        _refreshCancellation = null;
    }

    private void LocalizationOnLanguageChanged()
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() =>
        {
            foreach (var row in _rowCache.Values)
            {
                row.Relocalize();
            }

            RenderStatus();
        });
    }

    private async void ApplyFiltersOnClick(object sender, RoutedEventArgs e) =>
        await LoadProfilesAsync().ConfigureAwait(true);

    private async void ClearFiltersOnClick(object sender, RoutedEventArgs e)
    {
        SearchTextBox.Text = string.Empty;
        GroupTextBox.Text = string.Empty;
        TagTextBox.Text = string.Empty;
        EnvironmentTextBox.Text = string.Empty;
        FavoritesOnlyCheckBox.IsChecked = false;
        await LoadProfilesAsync().ConfigureAwait(true);
    }

    private async Task LoadProfilesAsync()
    {
        if (_isRefreshing)
        {
            return;
        }

        _profileLoadCancellation?.Cancel();
        _profileLoadCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _profileLoadCancellation = cancellation;
        _isLoading = true;
        SetStatus("Loc.GlobalDashboard.Loading");
        UpdateButtons();

        try
        {
            var filter = new ServerProfileSearchFilter(
                SearchTextBox.Text,
                GroupTextBox.Text,
                TagTextBox.Text,
                EnvironmentTextBox.Text,
                FavoritesOnlyCheckBox.IsChecked == true);
            var organizedProfiles = await _organizationService
                .SearchAsync(filter, cancellation.Token)
                .ConfigureAwait(true);
            if (cancellation.IsCancellationRequested || !ReferenceEquals(_profileLoadCancellation, cancellation))
            {
                return;
            }

            var targets = GetCurrentTargets();
            Rows.Clear();
            foreach (var item in organizedProfiles)
            {
                var state = targets.TryGetValue(item.Profile.Id, out var target)
                    ? target.ConnectionState
                    : RemoteSessionState.Disconnected;
                if (!_rowCache.TryGetValue(item.Profile.Id, out var row))
                {
                    row = new GlobalDashboardRowViewModel(item, state, _localization);
                    _rowCache.Add(item.Profile.Id, row);
                }
                else
                {
                    row.UpdateIdentity(item, state);
                }

                Rows.Add(row);
            }

            SetStatus(
                Rows.Count == 0 ? "Loc.GlobalDashboard.Empty" : "Loc.GlobalDashboard.Loaded",
                Rows.Count);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_profileLoadCancellation, cancellation))
            {
                SetStatus("Loc.GlobalDashboard.LoadCancelled");
            }
        }
        catch
        {
            if (ReferenceEquals(_profileLoadCancellation, cancellation))
            {
                SetStatus("Loc.GlobalDashboard.LoadFailed");
            }
        }
        finally
        {
            if (ReferenceEquals(_profileLoadCancellation, cancellation))
            {
                _profileLoadCancellation = null;
                _isLoading = false;
                UpdateButtons();
            }

            cancellation.Dispose();
        }
    }

    private async void RefreshOnClick(object sender, RoutedEventArgs e)
    {
        if (_isRefreshing || Rows.Count == 0)
        {
            return;
        }

        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _refreshCancellation = cancellation;
        _isRefreshing = true;
        SyncConnectionStates();
        UpdateButtons();

        var targets = Rows
            .Select(row => new MultiServerDashboardTarget(row.Profile, row.IsConnected))
            .ToArray();
        SetStatus(
            "Loc.GlobalDashboard.RefreshingSummary",
            targets.Count(target => target.IsConnected),
            targets.Length);

        try
        {
            await _refreshService.RefreshAsync(
                targets,
                update => PublishUpdateAsync(update, cancellation),
                cancellation.Token).ConfigureAwait(true);

            if (!cancellation.IsCancellationRequested && ReferenceEquals(_refreshCancellation, cancellation))
            {
                var completed = Rows.Count(row => row.HasAvailableSnapshot);
                var failed = Rows.Count(row => row.HealthState == GlobalDashboardHealthState.Failed);
                SetStatus("Loc.GlobalDashboard.RefreshComplete", completed, failed, Rows.Count);
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
            }

            cancellation.Dispose();
        }
    }

    private ValueTask PublishUpdateAsync(
        MultiServerDashboardUpdate update,
        CancellationToken refreshCancellation)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return ValueTask.CompletedTask;
        }

        return new ValueTask(Dispatcher.InvokeAsync(() =>
        {
            if (refreshCancellation.IsCancellationRequested &&
                update.State is not MultiServerDashboardUpdateState.Cancelled)
            {
                return;
            }

            if (_rowCache.TryGetValue(update.ServerProfileId, out var row))
            {
                row.Apply(update);
            }
        }).Task);
    }

    private void CancelOnClick(object sender, RoutedEventArgs e)
    {
        if (_refreshCancellation is null)
        {
            return;
        }

        _refreshCancellation.Cancel();
        SetStatus("Loc.GlobalDashboard.Cancelling");
    }

    private void SyncConnectionStates()
    {
        var targets = GetCurrentTargets();
        foreach (var row in Rows)
        {
            row.SetConnectionState(
                targets.TryGetValue(row.Profile.Id, out var target)
                    ? target.ConnectionState
                    : RemoteSessionState.Disconnected);
        }
    }

    private IReadOnlyDictionary<Guid, GlobalDashboardTarget> GetCurrentTargets() =>
        _targetsProvider()
            .Where(target => target.Profile is not null)
            .GroupBy(target => target.Profile.Id)
            .ToDictionary(group => group.Key, group => group.First());

    private void SetStatus(string key, params object?[] arguments)
    {
        _statusKey = key;
        _statusArguments = arguments;
        RenderStatus();
    }

    private void RenderStatus()
    {
        StatusText.Text = _statusArguments.Length == 0
            ? _localization.Get(_statusKey)
            : _localization.Format(_statusKey, _statusArguments);
    }

    private void UpdateButtons()
    {
        RefreshButton.IsEnabled = !_isRefreshing && !_isLoading && Rows.Count > 0;
        CancelButton.IsEnabled = _isRefreshing;
        ApplyButton.IsEnabled = !_isRefreshing && !_isLoading;
        ClearButton.IsEnabled = !_isRefreshing && !_isLoading;
    }
}

public enum GlobalDashboardHealthState
{
    NotRefreshed,
    Disconnected,
    Refreshing,
    Healthy,
    Warning,
    Critical,
    Failed,
    Cancelled,
}

public sealed class GlobalDashboardRowViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;
    private ServerProfileOrganization _organization;
    private RemoteSessionState _connectionState;
    private GlobalDashboardHealthState _healthState;
    private ServerDashboardSnapshot? _snapshot;
    private string? _healthError;

    public GlobalDashboardRowViewModel(
        OrganizedServerProfile item,
        RemoteSessionState connectionState,
        ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(item);
        Profile = item.Profile;
        _organization = item.Organization;
        _connectionState = connectionState;
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _healthState = connectionState == RemoteSessionState.Connected
            ? GlobalDashboardHealthState.NotRefreshed
            : GlobalDashboardHealthState.Disconnected;
    }

    public ServerProfile Profile { get; private set; }

    public string Name => Profile.Name;

    public string Endpoint => $"{Profile.Username}@{Profile.Host}:{Profile.Port}";

    public string EnvironmentDisplay => Profile.Environment ?? _localization.Get("Loc.GlobalDashboard.Unlabeled");

    public string GroupDisplay => _organization.GroupName ?? _localization.Get("Loc.GlobalDashboard.Ungrouped");

    public string TagsDisplay => _organization.Tags.Count == 0
        ? _localization.Get("Loc.GlobalDashboard.NoTags")
        : string.Join(", ", _organization.Tags);

    public string FavoriteDisplay => _organization.IsFavorite ? "★" : "—";

    public bool IsConnected => _connectionState == RemoteSessionState.Connected;

    public string ConnectionDisplay => _localization.Get(_connectionState switch
    {
        RemoteSessionState.Connecting => "Loc.GlobalDashboard.ConnectionConnecting",
        RemoteSessionState.Connected => "Loc.GlobalDashboard.ConnectionConnected",
        RemoteSessionState.Reconnecting => "Loc.GlobalDashboard.ConnectionReconnecting",
        RemoteSessionState.Disconnecting => "Loc.GlobalDashboard.ConnectionDisconnecting",
        RemoteSessionState.Faulted => "Loc.GlobalDashboard.ConnectionFailed",
        _ => "Loc.GlobalDashboard.ConnectionDisconnected",
    });

    public GlobalDashboardHealthState HealthState => _healthState;

    public bool HasAvailableSnapshot => _snapshot is not null;

    public string HealthDisplay => _localization.Get(_healthState switch
    {
        GlobalDashboardHealthState.Disconnected => "Loc.GlobalDashboard.HealthDisconnected",
        GlobalDashboardHealthState.Refreshing => "Loc.GlobalDashboard.HealthRefreshing",
        GlobalDashboardHealthState.Healthy => "Loc.GlobalDashboard.HealthHealthy",
        GlobalDashboardHealthState.Warning => "Loc.GlobalDashboard.HealthWarning",
        GlobalDashboardHealthState.Critical => "Loc.GlobalDashboard.HealthCritical",
        GlobalDashboardHealthState.Failed => "Loc.GlobalDashboard.HealthFailed",
        GlobalDashboardHealthState.Cancelled => "Loc.GlobalDashboard.HealthCancelled",
        _ => "Loc.GlobalDashboard.HealthNotRefreshed",
    });

    public string HealthDetail
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_healthError))
            {
                return _healthError;
            }

            if (_snapshot is not null)
            {
                return _localization.Format(
                    "Loc.GlobalDashboard.Captured",
                    _snapshot.CapturedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
            }

            return _healthState == GlobalDashboardHealthState.Disconnected
                ? _localization.Get("Loc.GlobalDashboard.DisconnectedDetail")
                : _localization.Get("Loc.GlobalDashboard.NotRefreshedDetail");
        }
    }

    public string CpuDisplay => _snapshot?.Cpu.Value is { } cpu
        ? $"{cpu.UtilizationPercent:F1}%"
        : "—";

    public string MemoryDisplay => _snapshot?.Memory.Value is { } memory
        ? $"{memory.UsedPercent:F1}%"
        : "—";

    public string DiskDisplay
    {
        get
        {
            var fileSystems = _snapshot?.FileSystems.Value;
            if (fileSystems is null || fileSystems.Count == 0)
            {
                return "—";
            }

            return $"{fileSystems.Max(fileSystem => fileSystem.UsedPercent):F1}%";
        }
    }

    public string WarningsDisplay
    {
        get
        {
            if (_snapshot is null)
            {
                return "—";
            }

            var critical = _snapshot.Warnings.Count(warning => warning.Severity == DashboardWarningSeverity.Critical);
            return critical == 0
                ? _snapshot.Warnings.Count.ToString(CultureInfo.CurrentCulture)
                : _localization.Format("Loc.GlobalDashboard.WarningSummary", _snapshot.Warnings.Count, critical);
        }
    }

    public void UpdateIdentity(OrganizedServerProfile item, RemoteSessionState connectionState)
    {
        ArgumentNullException.ThrowIfNull(item);
        Profile = item.Profile;
        _organization = item.Organization;
        SetConnectionState(connectionState);
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Endpoint));
        OnPropertyChanged(nameof(EnvironmentDisplay));
        OnPropertyChanged(nameof(GroupDisplay));
        OnPropertyChanged(nameof(TagsDisplay));
        OnPropertyChanged(nameof(FavoriteDisplay));
    }

    public void SetConnectionState(RemoteSessionState connectionState)
    {
        var wasConnected = _connectionState == RemoteSessionState.Connected;
        _connectionState = connectionState;
        var isConnected = connectionState == RemoteSessionState.Connected;
        if (!isConnected)
        {
            _snapshot = null;
            _healthError = null;
            _healthState = GlobalDashboardHealthState.Disconnected;
        }
        else if (!wasConnected && _healthState == GlobalDashboardHealthState.Disconnected)
        {
            _healthState = GlobalDashboardHealthState.NotRefreshed;
        }

        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(ConnectionDisplay));
        NotifyHealthChanged();
    }

    public void Apply(MultiServerDashboardUpdate update)
    {
        if (update.ServerProfileId != Profile.Id)
        {
            return;
        }

        _healthError = null;
        switch (update.State)
        {
            case MultiServerDashboardUpdateState.Disconnected:
                _snapshot = null;
                _healthState = GlobalDashboardHealthState.Disconnected;
                break;
            case MultiServerDashboardUpdateState.Refreshing:
                _healthState = GlobalDashboardHealthState.Refreshing;
                break;
            case MultiServerDashboardUpdateState.Available when update.Snapshot is not null:
                _snapshot = update.Snapshot;
                _healthState = ResolveHealth(update.Snapshot.Warnings);
                break;
            case MultiServerDashboardUpdateState.Failed:
                _snapshot = null;
                _healthError = update.Error?.Message ?? _localization.Get("Loc.GlobalDashboard.GenericFailure");
                _healthState = GlobalDashboardHealthState.Failed;
                break;
            case MultiServerDashboardUpdateState.Cancelled:
                _snapshot = null;
                _healthState = GlobalDashboardHealthState.Cancelled;
                break;
        }

        NotifyHealthChanged();
    }

    public void Relocalize()
    {
        OnPropertyChanged(nameof(EnvironmentDisplay));
        OnPropertyChanged(nameof(GroupDisplay));
        OnPropertyChanged(nameof(TagsDisplay));
        OnPropertyChanged(nameof(ConnectionDisplay));
        NotifyHealthChanged();
    }

    private static GlobalDashboardHealthState ResolveHealth(IReadOnlyList<DashboardHealthWarning> warnings)
    {
        if (warnings.Any(warning => warning.Severity == DashboardWarningSeverity.Critical))
        {
            return GlobalDashboardHealthState.Critical;
        }

        return warnings.Count > 0
            ? GlobalDashboardHealthState.Warning
            : GlobalDashboardHealthState.Healthy;
    }

    private void NotifyHealthChanged()
    {
        OnPropertyChanged(nameof(HealthState));
        OnPropertyChanged(nameof(HasAvailableSnapshot));
        OnPropertyChanged(nameof(HealthDisplay));
        OnPropertyChanged(nameof(HealthDetail));
        OnPropertyChanged(nameof(CpuDisplay));
        OnPropertyChanged(nameof(MemoryDisplay));
        OnPropertyChanged(nameof(DiskDisplay));
        OnPropertyChanged(nameof(WarningsDisplay));
    }
}
