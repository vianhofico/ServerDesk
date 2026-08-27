using ServerDesk.Application.Settings;

namespace ServerDesk.App.Presentation;

public sealed record NavigationItem(
    string Route,
    string Title,
    string Description,
    bool IsAvailable = true);

public interface INavigationService
{
    NavigationItem Current { get; }

    event Action<NavigationItem>? CurrentChanged;

    void Navigate(NavigationItem item);
}

public sealed class NavigationService : INavigationService
{
    public NavigationService()
    {
        Current = new NavigationItem(
            "dashboard",
            "Dashboard",
            "ServerDesk foundation is ready for secure remote capabilities.");
    }

    public NavigationItem Current { get; private set; }

    public event Action<NavigationItem>? CurrentChanged;

    public void Navigate(NavigationItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.IsAvailable || item == Current)
        {
            return;
        }

        Current = item;
        CurrentChanged?.Invoke(Current);
    }
}

public sealed class ShellViewModel : ObservableObject
{
    private readonly INavigationService _navigationService;
    private readonly IThemeService _themeService;
    private readonly IAppSettingsStore _settingsStore;
    private NavigationItem _selectedNavigationItem;
    private AppThemePreference _themePreference;
    private string? _settingsMessage;

    public ShellViewModel(
        INavigationService navigationService,
        IThemeService themeService,
        IAppSettingsStore settingsStore)
    {
        _navigationService = navigationService;
        _themeService = themeService;
        _settingsStore = settingsStore;

        NavigationItems =
        [
            new("dashboard", "Dashboard", "Foundation health, local configuration and future server summary."),
            new("explorer", "Explorer", "Remote SFTP file management arrives in M1/M2.", false),
            new("terminal", "Terminal", "Interactive SSH PTY arrives in M1.", false),
            new("processes", "Processes", "Task-Manager-like process management arrives in M2.", false),
            new("services", "Services", "systemd service management arrives in M2.", false),
            new("docker", "Docker", "Docker and Compose management arrives in M3.", false),
            new("storage", "Storage", "Filesystem and disk analysis arrives in M2.", false),
            new("network", "Network", "Interfaces, ports and tunnels arrive in M1/M2.", false),
            new("logs", "Logs", "journald and file log views arrive in M2.", false),
        ];

        ThemeOptions = Enum.GetValues<AppThemePreference>();
        _selectedNavigationItem = NavigationItems[0];
        _themePreference = AppThemePreference.System;
        _navigationService.CurrentChanged += OnCurrentNavigationChanged;
    }

    public IReadOnlyList<NavigationItem> NavigationItems { get; }

    public IReadOnlyList<AppThemePreference> ThemeOptions { get; }

    public NavigationItem SelectedNavigationItem
    {
        get => _selectedNavigationItem;
        set
        {
            if (value is null || !SetProperty(ref _selectedNavigationItem, value))
            {
                return;
            }

            _navigationService.Navigate(value);
            OnPropertyChanged(nameof(CurrentTitle));
            OnPropertyChanged(nameof(CurrentDescription));
        }
    }

    public string CurrentTitle => _navigationService.Current.Title;

    public string CurrentDescription => _navigationService.Current.Description;

    public AppThemePreference ThemePreference
    {
        get => _themePreference;
        set
        {
            if (!SetProperty(ref _themePreference, value))
            {
                return;
            }

            _themeService.Apply(value);
            OnPropertyChanged(nameof(EffectiveTheme));
            _ = PersistThemePreferenceAsync(value);
        }
    }

    public string EffectiveTheme => _themeService.EffectiveTheme.ToString();

    public string? SettingsMessage
    {
        get => _settingsMessage;
        private set => SetProperty(ref _settingsMessage, value);
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(true);
        _themePreference = settings.ThemePreference;
        OnPropertyChanged(nameof(ThemePreference));
        _themeService.Apply(_themePreference);
        OnPropertyChanged(nameof(EffectiveTheme));
    }

    private async Task PersistThemePreferenceAsync(AppThemePreference preference)
    {
        try
        {
            await _settingsStore.SaveAsync(new AppSettings(preference)).ConfigureAwait(true);
            SettingsMessage = null;
        }
        catch (IOException)
        {
            SettingsMessage = "Theme changed for this session, but the preference could not be saved.";
        }
        catch (UnauthorizedAccessException)
        {
            SettingsMessage = "Theme changed for this session, but the preference could not be saved.";
        }
    }

    private void OnCurrentNavigationChanged(NavigationItem item)
    {
        _selectedNavigationItem = item;
        OnPropertyChanged(nameof(SelectedNavigationItem));
        OnPropertyChanged(nameof(CurrentTitle));
        OnPropertyChanged(nameof(CurrentDescription));
    }
}
