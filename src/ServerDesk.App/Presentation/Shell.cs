using System.Collections.ObjectModel;
using System.Globalization;
using ServerDesk.Application.Profiles;
using ServerDesk.Application.Sessions;
using ServerDesk.Application.Settings;
using ServerDesk.Domain.Errors;
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
            "Manage secure server profiles and SSH sessions.");
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
                ? "SSH-agent profiles are retained, but the current transport does not connect them yet."
                : "The SSH server will request interactive responses when you connect.";
        }
    }

    public static ProfileEditorViewModel CreateNew() => new(null);

    public static ProfileEditorViewModel FromProfile(ServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new ProfileEditorViewModel(profile);
    }
}

public sealed class ServerProfileListItemViewModel : ObservableObject
{
    private RemoteSessionState _connectionState = RemoteSessionState.Disconnected;
    private RemoteError? _connectionError;
    private string? _serverVersion;
    private DateTimeOffset? _connectedAtUtc;

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

    public RemoteSessionState ConnectionState => _connectionState;

    public string ConnectionLabel => _connectionState switch
    {
        RemoteSessionState.Created or RemoteSessionState.Disconnected => "Not connected",
        RemoteSessionState.Connecting => "Connecting…",
        RemoteSessionState.Connected => "Connected",
        RemoteSessionState.Reconnecting => "Reconnecting…",
        RemoteSessionState.Disconnecting => "Disconnecting…",
        RemoteSessionState.Faulted => "Connection failed",
        _ => "Not connected",
    };

    public string ConnectionDetail
    {
        get
        {
            if (_connectionError is not null && _connectionState is RemoteSessionState.Faulted or RemoteSessionState.Disconnected)
            {
                return _connectionError.Message;
            }

            return _connectionState switch
            {
                RemoteSessionState.Connecting => "Verifying the SSH host identity, then authenticating.",
                RemoteSessionState.Reconnecting => "Starting a fresh SSH session. Remote mutations are never retried automatically.",
                RemoteSessionState.Connected => string.IsNullOrWhiteSpace(_serverVersion)
                    ? "Secure SSH control session established."
                    : $"Secure SSH control session established · {_serverVersion}",
                RemoteSessionState.Disconnecting => "Closing the SSH control session without blocking the UI.",
                _ => Profile.AuthenticationKind == ServerAuthenticationKind.SshAgent
                    ? "This profile uses SSH agent authentication, which is not supported by the current transport yet."
                    : "Ready. Host identity must be trusted before authentication is allowed.",
            };
        }
    }

    public string ServerVersionDisplay => string.IsNullOrWhiteSpace(_serverVersion) ? "—" : _serverVersion;

    public string ConnectedSinceDisplay => _connectedAtUtc is null
        ? "—"
        : _connectedAtUtc.Value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    public bool CanConnect =>
        Profile.AuthenticationKind != ServerAuthenticationKind.SshAgent &&
        _connectionState is RemoteSessionState.Created or RemoteSessionState.Disconnected or RemoteSessionState.Faulted;

    public bool CanDisconnect =>
        _connectionState is RemoteSessionState.Connected or RemoteSessionState.Connecting or RemoteSessionState.Reconnecting;

    public bool CanModifyProfile =>
        _connectionState is RemoteSessionState.Created or RemoteSessionState.Disconnected or RemoteSessionState.Faulted;

    public string ConnectActionLabel => _connectionState == RemoteSessionState.Faulted ? "Reconnect" : "Connect";

    public string DisconnectActionLabel =>
        _connectionState is RemoteSessionState.Connecting or RemoteSessionState.Reconnecting ? "Cancel" : "Disconnect";

    public void ApplySession(IRemoteSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _connectionState = session.State;
        _connectionError = session.LastError;
        _serverVersion = session.ServerVersion;
        _connectedAtUtc = session.ConnectedAtUtc;

        OnPropertyChanged(nameof(ConnectionState));
        OnPropertyChanged(nameof(ConnectionLabel));
        OnPropertyChanged(nameof(ConnectionDetail));
        OnPropertyChanged(nameof(ServerVersionDisplay));
        OnPropertyChanged(nameof(ConnectedSinceDisplay));
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanDisconnect));
        OnPropertyChanged(nameof(CanModifyProfile));
        OnPropertyChanged(nameof(ConnectActionLabel));
        OnPropertyChanged(nameof(DisconnectActionLabel));
    }
}

public sealed class ShellViewModel : ObservableObject
{
    private readonly INavigationService _navigationService;
    private readonly IThemeService _themeService;
    private readonly IAppSettingsStore _settingsStore;
    private readonly IServerProfileService _serverProfileService;
    private readonly IRemoteSessionFactory _remoteSessionFactory;
    private readonly SynchronizationContext? _uiContext;
    private readonly Dictionary<Guid, IRemoteSession> _sessions = [];
    private readonly Dictionary<Guid, Action<RemoteSessionState>> _sessionHandlers = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _connectionCancellations = [];
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
        IServerProfileService serverProfileService,
        IRemoteSessionFactory remoteSessionFactory)
    {
        _navigationService = navigationService;
        _themeService = themeService;
        _settingsStore = settingsStore;
        _serverProfileService = serverProfileService;
        _remoteSessionFactory = remoteSessionFactory;
        _uiContext = SynchronizationContext.Current;

        NavigationItems =
        [
            new("dashboard", "Dashboard", "Server profiles and secure SSH connection state."),
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
        EditServerCommand = new RelayCommand(
            BeginEditServer,
            () => SelectedServer?.CanModifyProfile == true && !IsBusy);
        RequestDeleteServerCommand = new RelayCommand(
            RequestDeleteServer,
            () => SelectedServer?.CanModifyProfile == true && !IsBusy);
        CancelDeleteServerCommand = new RelayCommand(() => IsDeleteConfirmationVisible = false);
        ConfirmDeleteServerCommand = new AsyncRelayCommand(
            DeleteSelectedServerAsync,
            () => SelectedServer?.CanModifyProfile == true && !IsBusy);
        SaveServerCommand = new AsyncRelayCommand(SaveServerAsync, () => Editor is not null && !IsBusy);
        CancelEditorCommand = new RelayCommand(CancelEditor, () => Editor is not null && !IsBusy);
        ConnectServerCommand = new AsyncRelayCommand(
            ConnectSelectedServerAsync,
            () => SelectedServer?.CanConnect == true && Editor is null && !IsBusy);
        DisconnectServerCommand = new AsyncRelayCommand(
            DisconnectSelectedServerAsync,
            () => SelectedServer?.CanDisconnect == true && Editor is null);
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

    public AsyncRelayCommand ConnectServerCommand { get; }

    public AsyncRelayCommand DisconnectServerCommand { get; }

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

    public async ValueTask ShutdownAsync()
    {
        foreach (var cancellation in _connectionCancellations.Values)
        {
            cancellation.Cancel();
        }

        foreach (var id in _sessions.Keys.ToArray())
        {
            await DisposeCachedSessionAsync(id).ConfigureAwait(false);
        }
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
        if (SelectedServer?.CanModifyProfile != true)
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
        if (SelectedServer?.CanModifyProfile == true)
        {
            IsDeleteConfirmationVisible = true;
            ErrorMessage = null;
        }
    }

    private async Task ConnectSelectedServerAsync()
    {
        var selected = SelectedServer;
        if (selected?.CanConnect != true)
        {
            return;
        }

        ErrorMessage = null;
        StatusMessage = null;
        var session = GetOrCreateSession(selected);
        var cancellation = new CancellationTokenSource();
        _connectionCancellations[selected.Id] = cancellation;

        try
        {
            await session.ConnectAsync(cancellation.Token).ConfigureAwait(true);
            selected.ApplySession(session);
            StatusMessage = $"Connected securely to {selected.Name}.";
        }
        catch (OperationCanceledException)
        {
            selected.ApplySession(session);
            StatusMessage = $"Connection to {selected.Name} was cancelled.";
        }
        catch (RemoteSessionException exception)
        {
            selected.ApplySession(session);
            ErrorMessage = exception.Error.Message;
        }
        catch (Exception exception)
        {
            selected.ApplySession(session);
            ErrorMessage = $"Could not connect to {selected.Name}: {exception.Message}";
        }
        finally
        {
            if (_connectionCancellations.Remove(selected.Id, out var currentCancellation))
            {
                currentCancellation.Dispose();
            }

            RaiseCommandStates();
        }
    }

    private async Task DisconnectSelectedServerAsync()
    {
        var selected = SelectedServer;
        if (selected is null)
        {
            return;
        }

        if (selected.ConnectionState is RemoteSessionState.Connecting or RemoteSessionState.Reconnecting)
        {
            if (_connectionCancellations.TryGetValue(selected.Id, out var connectionCancellation))
            {
                connectionCancellation.Cancel();
                StatusMessage = $"Cancelling connection to {selected.Name}…";
            }

            return;
        }

        if (!_sessions.TryGetValue(selected.Id, out var session))
        {
            return;
        }

        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            await session.DisconnectAsync().ConfigureAwait(true);
            selected.ApplySession(session);
            StatusMessage = $"Disconnected from {selected.Name}.";
        }
        catch (RemoteSessionException exception)
        {
            selected.ApplySession(session);
            ErrorMessage = exception.Error.Message;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            selected.ApplySession(session);
            ErrorMessage = $"Could not disconnect cleanly from {selected.Name}: {exception.Message}";
        }
        finally
        {
            RaiseCommandStates();
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
                await DisposeCachedSessionAsync(Editor.ProfileId.Value).ConfigureAwait(true);
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
        if (SelectedServer?.CanModifyProfile != true)
        {
            return;
        }

        var selectedId = SelectedServer.Id;
        var selectedName = SelectedServer.Name;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await DisposeCachedSessionAsync(selectedId).ConfigureAwait(true);
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

    private IRemoteSession GetOrCreateSession(ServerProfileListItemViewModel item)
    {
        if (_sessions.TryGetValue(item.Id, out var existing))
        {
            return existing;
        }

        var session = _remoteSessionFactory.Create(item.Profile);
        Action<RemoteSessionState> handler = _ => OnSessionStateChanged(item.Id, session);
        session.StateChanged += handler;
        _sessions[item.Id] = session;
        _sessionHandlers[item.Id] = handler;
        item.ApplySession(session);
        return session;
    }

    private void OnSessionStateChanged(Guid serverId, IRemoteSession session)
    {
        void Apply()
        {
            var item = Servers.FirstOrDefault(server => server.Id == serverId);
            item?.ApplySession(session);
            if (SelectedServer?.Id == serverId)
            {
                if (session.LastError is not null && session.State == RemoteSessionState.Faulted)
                {
                    ErrorMessage = session.LastError.Message;
                }

                RaiseCommandStates();
            }
        }

        if (_uiContext is null || ReferenceEquals(SynchronizationContext.Current, _uiContext))
        {
            Apply();
            return;
        }

        _uiContext.Post(_ => Apply(), null);
    }

    private async ValueTask DisposeCachedSessionAsync(Guid serverId)
    {
        if (_connectionCancellations.Remove(serverId, out var connectionCancellation))
        {
            connectionCancellation.Cancel();
            connectionCancellation.Dispose();
        }

        if (!_sessions.Remove(serverId, out var session))
        {
            return;
        }

        if (_sessionHandlers.Remove(serverId, out var handler))
        {
            session.StateChanged -= handler;
        }

        if (session.State == RemoteSessionState.Connected)
        {
            using var disconnectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await session.DisconnectAsync(disconnectTimeout.Token).ConfigureAwait(false);
            }
            catch
            {
                // Disposal below is the final local cleanup path.
            }
        }

        await session.DisposeAsync().ConfigureAwait(false);
    }

    private async Task LoadProfilesAsync(Guid? selectedId, CancellationToken cancellationToken = default)
    {
        var profiles = await _serverProfileService.ListAsync(cancellationToken).ConfigureAwait(true);
        Servers.Clear();
        foreach (var profile in profiles)
        {
            var item = new ServerProfileListItemViewModel(profile);
            if (_sessions.TryGetValue(profile.Id, out var session))
            {
                item.ApplySession(session);
            }

            Servers.Add(item);
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
        ConnectServerCommand.RaiseCanExecuteChanged();
        DisconnectServerCommand.RaiseCanExecuteChanged();
    }
}
