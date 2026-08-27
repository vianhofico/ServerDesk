using System.Collections.ObjectModel;
using System.Globalization;
using ServerDesk.Application.Profiles;
using ServerDesk.Application.Settings;
using ServerDesk.Domain.Servers;

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
            "Servers",
            "Manage secure server profiles before connecting through SSH.");
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

public sealed record AuthenticationChoice(ServerAuthenticationKind Kind, string DisplayName);

public sealed class ProfileEditorViewModel : ObservableObject
{
    private string _name;
    private string _host;
    private string _portText;
    private string _username;
    private string _environment;
    private ServerAuthenticationKind _authenticationKind;
    private string _privateKeyPath;
    private string _newSecret = string.Empty;

    private ProfileEditorViewModel(ServerProfile? profile)
    {
        ProfileId = profile?.Id;
        _name = profile?.Name ?? string.Empty;
        _host = profile?.Host ?? string.Empty;
        _portText = (profile?.Port ?? 22).ToString(CultureInfo.InvariantCulture);
        _username = profile?.Username ?? string.Empty;
        _environment = profile?.Environment ?? string.Empty;
        _authenticationKind = profile?.AuthenticationKind ?? ServerAuthenticationKind.Password;
        _privateKeyPath = profile?.PrivateKeyPath ?? string.Empty;
        HasStoredCredential = profile?.CredentialReference is not null;
    }

    private static IReadOnlyList<AuthenticationChoice> AuthenticationOptions { get; } =
    [
        new(ServerAuthenticationKind.Password, "Password"),
        new(ServerAuthenticationKind.PrivateKey, "Private key"),
        new(ServerAuthenticationKind.SshAgent, "SSH agent"),
        new(ServerAuthenticationKind.KeyboardInteractive, "Keyboard interactive"),
    ];

    public Guid? ProfileId { get; }

    public bool IsNew => ProfileId is null;

    public bool HasStoredCredential { get; }

    public IReadOnlyList<AuthenticationChoice> AuthenticationChoices => AuthenticationOptions;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string Host
    {
        get => _host;
        set => SetProperty(ref _host, value);
    }

    public string PortText
    {
        get => _portText;
        set => SetProperty(ref _portText, value);
    }

    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value);
    }

    public string Environment
    {
        get => _environment;
        set => SetProperty(ref _environment, value);
    }

    public ServerAuthenticationKind AuthenticationKind
    {
        get => _authenticationKind;
        set
        {
            if (!SetProperty(ref _authenticationKind, value))
            {
                return;
            }

            NewSecret = string.Empty;
            OnPropertyChanged(nameof(IsPrivateKey));
            OnPropertyChanged(nameof(CanStoreSecret));
            OnPropertyChanged(nameof(SecretLabel));
            OnPropertyChanged(nameof(SecretHint));
        }
    }

    public string PrivateKeyPath
    {
        get => _privateKeyPath;
        set => SetProperty(ref _privateKeyPath, value);
    }

    public string NewSecret
    {
        get => _newSecret;
        set => SetProperty(ref _newSecret, value);
    }

    public bool IsPrivateKey => AuthenticationKind == ServerAuthenticationKind.PrivateKey;

    public bool CanStoreSecret =>
        AuthenticationKind is ServerAuthenticationKind.Password or ServerAuthenticationKind.PrivateKey;

    public string SecretLabel =>
        AuthenticationKind == ServerAuthenticationKind.PrivateKey ? "Key passphrase" : "Password";

    public string SecretHint
    {
        get
        {
            if (AuthenticationKind == ServerAuthenticationKind.PrivateKey)
            {
                return HasStoredCredential
                    ? "Leave blank to keep the existing passphrase."
                    : "Optional. Leave blank for an unencrypted private key.";
            }

            if (AuthenticationKind == ServerAuthenticationKind.Password)
            {
                return HasStoredCredential
                    ? "Leave blank to keep the existing password."
                    : "Required. Stored in Windows Credential Manager, not SQLite.";
            }

            return AuthenticationKind == ServerAuthenticationKind.SshAgent
                ? "ServerDesk will use your SSH agent in M1.3."
                : "Credentials will be requested interactively when connecting in M1.3.";
        }
    }

    public static ProfileEditorViewModel CreateNew() => new(null);

    public static ProfileEditorViewModel FromProfile(ServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new ProfileEditorViewModel(profile);
    }
}

public sealed class ServerProfileListItemViewModel
{
    public ServerProfileListItemViewModel(ServerProfile profile)
    {
        Profile = profile;
    }

    public ServerProfile Profile { get; }

    public Guid Id => Profile.Id;

    public string Name => Profile.Name;

    public string Endpoint => $"{Profile.Username}@{Profile.Host}:{Profile.Port}";

    public string Environment => Profile.Environment ?? "Unlabeled";

    public string AuthenticationLabel => Profile.AuthenticationKind switch
    {
        ServerAuthenticationKind.Password => "Password",
        ServerAuthenticationKind.PrivateKey => "Private key",
        ServerAuthenticationKind.SshAgent => "SSH agent",
        ServerAuthenticationKind.KeyboardInteractive => "Keyboard interactive",
        _ => "Unknown",
    };
}

public sealed class ShellViewModel : ObservableObject
{
    private readonly INavigationService _navigationService;
    private readonly IThemeService _themeService;
    private readonly IAppSettingsStore _settingsStore;
    private readonly IServerProfileService _serverProfileService;
    private NavigationItem _selectedNavigationItem;
    private ServerProfileListItemViewModel? _selectedServer;
    private ProfileEditorViewModel? _editor;
    private AppThemePreference _themePreference;
    private string? _settingsMessage;
    private string? _statusMessage;
    private string? _errorMessage;
    private bool _isBusy;
    private bool _isDeleteConfirmationVisible;

    public ShellViewModel(
        INavigationService navigationService,
        IThemeService themeService,
        IAppSettingsStore settingsStore,
        IServerProfileService serverProfileService)
    {
        _navigationService = navigationService;
        _themeService = themeService;
        _settingsStore = settingsStore;
        _serverProfileService = serverProfileService;

        NavigationItems =
        [
            new("dashboard", "Dashboard", "Server profiles and connection readiness."),
            new("explorer", "Explorer", "Remote SFTP file management arrives in M1.4.", false),
            new("terminal", "Terminal", "Interactive SSH PTY arrives in M1.5.", false),
            new("processes", "Processes", "Task-Manager-like process management arrives in M2.", false),
            new("services", "Services", "systemd service management arrives in M2.", false),
            new("docker", "Docker", "Docker and Compose management arrives in M3.", false),
            new("storage", "Storage", "Filesystem and disk analysis arrives in M2.", false),
            new("network", "Network", "Interfaces, ports and tunnels arrive in M1.6/M2.", false),
            new("logs", "Logs", "journald and file log views arrive in M2.", false),
        ];

        Servers = [];
        ThemeOptions = Enum.GetValues<AppThemePreference>();
        _selectedNavigationItem = NavigationItems[0];
        _themePreference = AppThemePreference.System;
        _navigationService.CurrentChanged += OnCurrentNavigationChanged;

        AddServerCommand = new RelayCommand(BeginAddServer, () => !IsBusy);
        EditServerCommand = new RelayCommand(BeginEditServer, () => SelectedServer is not null && !IsBusy);
        RequestDeleteServerCommand = new RelayCommand(RequestDeleteServer, () => SelectedServer is not null && !IsBusy);
        CancelDeleteServerCommand = new RelayCommand(() => IsDeleteConfirmationVisible = false);
        ConfirmDeleteServerCommand = new AsyncRelayCommand(DeleteSelectedServerAsync, () => SelectedServer is not null && !IsBusy);
        SaveServerCommand = new AsyncRelayCommand(SaveServerAsync, () => Editor is not null && !IsBusy);
        CancelEditorCommand = new RelayCommand(CancelEditor, () => Editor is not null && !IsBusy);
    }

    public IReadOnlyList<NavigationItem> NavigationItems { get; }

    public ObservableCollection<ServerProfileListItemViewModel> Servers { get; }

    public IReadOnlyList<AppThemePreference> ThemeOptions { get; }

    public RelayCommand AddServerCommand { get; }

    public RelayCommand EditServerCommand { get; }

    public RelayCommand RequestDeleteServerCommand { get; }

    public RelayCommand CancelDeleteServerCommand { get; }

    public AsyncRelayCommand ConfirmDeleteServerCommand { get; }

    public AsyncRelayCommand SaveServerCommand { get; }

    public RelayCommand CancelEditorCommand { get; }

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

    public ServerProfileListItemViewModel? SelectedServer
    {
        get => _selectedServer;
        set
        {
            if (!SetProperty(ref _selectedServer, value))
            {
                return;
            }

            IsDeleteConfirmationVisible = false;
            OnPropertyChanged(nameof(HasSelectedServer));
            OnPropertyChanged(nameof(HasNoSelectedServer));
            RaiseCommandStates();
        }
    }

    public ProfileEditorViewModel? Editor
    {
        get => _editor;
        private set
        {
            if (!SetProperty(ref _editor, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsEditorOpen));
            OnPropertyChanged(nameof(IsDashboardVisible));
            RaiseCommandStates();
        }
    }

    public bool HasSelectedServer => SelectedServer is not null;

    public bool HasNoSelectedServer => SelectedServer is null;

    public bool IsEditorOpen => Editor is not null;

    public bool IsDashboardVisible => Editor is null;

    public string CurrentTitle => _navigationService.Current.Title;

    public string CurrentDescription => _navigationService.Current.Description;

    public string ServerCountLabel => Servers.Count == 1 ? "1 server" : $"{Servers.Count} servers";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool IsDeleteConfirmationVisible
    {
        get => _isDeleteConfirmationVisible;
        private set => SetProperty(ref _isDeleteConfirmationVisible, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatusMessage));
            }
        }
    }

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasErrorMessage));
            }
        }
    }

    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);

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
        await LoadProfilesAsync(selectedId: null, cancellationToken).ConfigureAwait(true);
    }

    private void BeginAddServer()
    {
        ErrorMessage = null;
        StatusMessage = null;
        IsDeleteConfirmationVisible = false;
        Editor = ProfileEditorViewModel.CreateNew();
    }

    private void BeginEditServer()
    {
        if (SelectedServer is null)
        {
            return;
        }

        ErrorMessage = null;
        StatusMessage = null;
        IsDeleteConfirmationVisible = false;
        Editor = ProfileEditorViewModel.FromProfile(SelectedServer.Profile);
    }

    private void CancelEditor()
    {
        if (Editor is not null)
        {
            Editor.NewSecret = string.Empty;
        }

        Editor = null;
        ErrorMessage = null;
    }

    private void RequestDeleteServer()
    {
        if (SelectedServer is not null)
        {
            IsDeleteConfirmationVisible = true;
            ErrorMessage = null;
        }
    }

    private async Task SaveServerAsync()
    {
        if (Editor is null)
        {
            return;
        }

        ErrorMessage = null;
        StatusMessage = null;
        if (!int.TryParse(Editor.PortText, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            ErrorMessage = "Port must be a number between 1 and 65535.";
            return;
        }

        IsBusy = true;
        try
        {
            var spec = new ServerProfileSpec(
                Editor.Name,
                Editor.Host,
                port,
                Editor.Username,
                Editor.Environment,
                Editor.AuthenticationKind,
                Editor.PrivateKeyPath);

            ServerProfile saved;
            if (Editor.ProfileId is null)
            {
                saved = await _serverProfileService.CreateAsync(spec, Editor.NewSecret).ConfigureAwait(true);
            }
            else
            {
                var replaceSecret = !string.IsNullOrEmpty(Editor.NewSecret);
                saved = await _serverProfileService.UpdateAsync(
                        Editor.ProfileId.Value,
                        spec,
                        Editor.NewSecret,
                        replaceSecret)
                    .ConfigureAwait(true);
            }

            Editor.NewSecret = string.Empty;
            Editor = null;
            await LoadProfilesAsync(saved.Id).ConfigureAwait(true);
            StatusMessage = "Server profile saved securely.";
        }
        catch (ServerProfileValidationException exception)
        {
            ErrorMessage = string.Join(" ", exception.Errors.Values.Distinct(StringComparer.Ordinal));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ErrorMessage = $"Could not save the server profile: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeleteSelectedServerAsync()
    {
        if (SelectedServer is null)
        {
            return;
        }

        var selectedId = SelectedServer.Id;
        var selectedName = SelectedServer.Name;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await _serverProfileService.DeleteAsync(selectedId).ConfigureAwait(true);
            IsDeleteConfirmationVisible = false;
            Editor = null;
            await LoadProfilesAsync(selectedId: null).ConfigureAwait(true);
            StatusMessage = $"Deleted {selectedName} and its stored credential.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ErrorMessage = $"Could not delete the server profile: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadProfilesAsync(Guid? selectedId, CancellationToken cancellationToken = default)
    {
        var profiles = await _serverProfileService.ListAsync(cancellationToken).ConfigureAwait(true);
        Servers.Clear();
        foreach (var profile in profiles)
        {
            Servers.Add(new ServerProfileListItemViewModel(profile));
        }

        SelectedServer = selectedId is null
            ? Servers.FirstOrDefault()
            : Servers.FirstOrDefault(server => server.Id == selectedId.Value) ?? Servers.FirstOrDefault();
        OnPropertyChanged(nameof(ServerCountLabel));
    }

    private async Task PersistThemePreferenceAsync(AppThemePreference preference)
    {
        try
        {
            await _settingsStore.SaveAsync(new AppSettings(preference)).ConfigureAwait(true);
            SettingsMessage = null;
        }
        catch (System.IO.IOException)
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

    private void RaiseCommandStates()
    {
        AddServerCommand.RaiseCanExecuteChanged();
        EditServerCommand.RaiseCanExecuteChanged();
        RequestDeleteServerCommand.RaiseCanExecuteChanged();
        ConfirmDeleteServerCommand.RaiseCanExecuteChanged();
        SaveServerCommand.RaiseCanExecuteChanged();
        CancelEditorCommand.RaiseCanExecuteChanged();
    }
}
