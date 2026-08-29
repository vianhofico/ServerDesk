using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ServerDesk.App.Localization;
using ServerDesk.Application.Databases;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class DatabaseProfilesWindow : Window
{
    private readonly IDatabaseProfileService _profileService;
    private readonly IDatabaseTunnelConnectivityService _connectivityService;
    private readonly ILocalizationService _localization;
    private readonly ServerProfile _serverProfile;
    private readonly ObservableCollection<DatabaseConnectionProfile> _profiles = [];
    private DatabaseConnectionProfile? _selectedProfile;
    private DatabaseEngineKind? _lastEngine;
    private bool _busy;
    private bool _loadingEditor;

    public DatabaseProfilesWindow(
        IDatabaseProfileService profileService,
        IDatabaseTunnelConnectivityService connectivityService,
        ILocalizationService localization,
        ServerProfile serverProfile)
    {
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _connectivityService = connectivityService ?? throw new ArgumentNullException(nameof(connectivityService));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _serverProfile = serverProfile ?? throw new ArgumentNullException(nameof(serverProfile));

        InitializeComponent();
        ProfilesGrid.ItemsSource = _profiles;
        EngineBox.ItemsSource = Enum.GetValues<DatabaseEngineKind>();
        AuthenticationBox.ItemsSource = Enum.GetValues<DatabaseAuthenticationKind>();
        _localization.LanguageChanged += LocalizationOnLanguageChanged;
        Closed += OnClosed;
        ApplyLocalizedState();
        ResetEditor();
        Loaded += async (_, _) => await RefreshAsync().ConfigureAwait(true);
    }

    private void OnClosed(object? sender, EventArgs e) =>
        _localization.LanguageChanged -= LocalizationOnLanguageChanged;

    private void LocalizationOnLanguageChanged() => ApplyLocalizedState();

    private void ApplyLocalizedState()
    {
        TitleText.Text = _localization.Format("Loc.DatabaseProfiles.Title", _serverProfile.Name);
        UpdatePasswordHint();
    }

    private async void RefreshOnClick(object sender, RoutedEventArgs e) =>
        await RefreshAsync(_selectedProfile?.Id).ConfigureAwait(true);

    private void NewOnClick(object sender, RoutedEventArgs e)
    {
        ProfilesGrid.SelectedItem = null;
        _selectedProfile = null;
        ResetEditor();
        StatusText.Text = _localization.Get("Loc.DatabaseProfiles.NewReady");
    }

    private async void SaveOnClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (!TryBuildSpec(out var spec, out var error))
        {
            StatusText.Text = error;
            return;
        }

        var password = PasswordBox.Password;
        SetBusy(true);
        try
        {
            DatabaseConnectionProfile saved;
            if (_selectedProfile is null)
            {
                saved = await _profileService.CreateAsync(
                        _serverProfile.Id,
                        spec!,
                        string.IsNullOrEmpty(password) ? null : password,
                        CancellationToken.None)
                    .ConfigureAwait(true);
            }
            else
            {
                saved = await _profileService.UpdateAsync(
                        _selectedProfile.Id,
                        spec!,
                        string.IsNullOrEmpty(password) ? null : password,
                        replaceSecret: !string.IsNullOrEmpty(password),
                        CancellationToken.None)
                    .ConfigureAwait(true);
            }

            PasswordBox.Clear();
            StatusText.Text = _localization.Get("Loc.DatabaseProfiles.Saved");
            await RefreshAsync(saved.Id).ConfigureAwait(true);
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

    private async void DeleteOnClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _selectedProfile is null)
        {
            StatusText.Text = _localization.Get("Loc.DatabaseProfiles.SelectFirst");
            return;
        }

        var confirmation = MessageBox.Show(
            _localization.Format("Loc.DatabaseProfiles.DeleteConfirm", _selectedProfile.Name),
            _localization.Get("Loc.DatabaseProfiles.DeleteTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true);
        try
        {
            await _profileService.DeleteAsync(_selectedProfile.Id, CancellationToken.None).ConfigureAwait(true);
            _selectedProfile = null;
            ResetEditor();
            StatusText.Text = _localization.Get("Loc.DatabaseProfiles.Deleted");
            await RefreshAsync().ConfigureAwait(true);
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

    private async void TestTunnelOnClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _selectedProfile is null)
        {
            StatusText.Text = _localization.Get("Loc.DatabaseProfiles.SelectFirst");
            return;
        }

        SetBusy(true);
        StatusText.Text = _localization.Get("Loc.DatabaseProfiles.TestingTunnel");
        try
        {
            var result = await _connectivityService.TestAsync(
                    _selectedProfile,
                    CancellationToken.None)
                .ConfigureAwait(true);
            StatusText.Text = result.IsSuccess && result.Endpoint is not null
                ? _localization.Format(
                    "Loc.DatabaseProfiles.TunnelSucceeded",
                    result.Endpoint.LocalHost,
                    result.Endpoint.LocalPort)
                : result.Message;
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

    private void ProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingEditor)
        {
            return;
        }

        _selectedProfile = ProfilesGrid.SelectedItem as DatabaseConnectionProfile;
        if (_selectedProfile is null)
        {
            return;
        }

        LoadEditor(_selectedProfile);
    }

    private void EngineSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingEditor || EngineBox.SelectedItem is not DatabaseEngineKind engine)
        {
            return;
        }

        var previousDefault = _lastEngine is null
            ? (int?)null
            : DatabaseConnectionProfile.DefaultPortFor(_lastEngine.Value);
        var currentIsDefault = previousDefault is not null &&
            int.TryParse(PortBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var currentPort) &&
            currentPort == previousDefault;
        if (string.IsNullOrWhiteSpace(PortBox.Text) || _selectedProfile is null || currentIsDefault)
        {
            PortBox.Text = DatabaseConnectionProfile.DefaultPortFor(engine).ToString(CultureInfo.InvariantCulture);
        }

        _lastEngine = engine;
    }

    private void AuthenticationSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingEditor)
        {
            return;
        }

        if (AuthenticationBox.SelectedItem is DatabaseAuthenticationKind.None)
        {
            PasswordBox.Clear();
        }

        UpdatePasswordHint();
    }

    private async Task RefreshAsync(Guid? selectId = null)
    {
        var profiles = await _profileService.ListForServerAsync(_serverProfile.Id, CancellationToken.None)
            .ConfigureAwait(true);
        _profiles.Clear();
        foreach (var profile in profiles)
        {
            _profiles.Add(profile);
        }

        var selected = selectId is null
            ? null
            : _profiles.FirstOrDefault(profile => profile.Id == selectId.Value);
        if (selected is not null)
        {
            ProfilesGrid.SelectedItem = selected;
            ProfilesGrid.ScrollIntoView(selected);
            _selectedProfile = selected;
            LoadEditor(selected);
        }
        else if (_selectedProfile is not null)
        {
            var stillPresent = _profiles.FirstOrDefault(profile => profile.Id == _selectedProfile.Id);
            ProfilesGrid.SelectedItem = stillPresent;
            _selectedProfile = stillPresent;
            if (stillPresent is not null)
            {
                LoadEditor(stillPresent);
            }
        }

        if (_profiles.Count == 0)
        {
            StatusText.Text = _localization.Get("Loc.DatabaseProfiles.Empty");
        }
    }

    private void ResetEditor()
    {
        _loadingEditor = true;
        try
        {
            NameBox.Text = string.Empty;
            EngineBox.SelectedItem = DatabaseEngineKind.PostgreSql;
            _lastEngine = DatabaseEngineKind.PostgreSql;
            HostBox.Text = "127.0.0.1";
            PortBox.Text = DatabaseConnectionProfile.DefaultPortFor(DatabaseEngineKind.PostgreSql)
                .ToString(CultureInfo.InvariantCulture);
            DatabaseNameBox.Text = string.Empty;
            UsernameBox.Text = string.Empty;
            AuthenticationBox.SelectedItem = DatabaseAuthenticationKind.Password;
            PasswordBox.Clear();
        }
        finally
        {
            _loadingEditor = false;
        }

        UpdatePasswordHint();
    }

    private void LoadEditor(DatabaseConnectionProfile profile)
    {
        _loadingEditor = true;
        try
        {
            NameBox.Text = profile.Name;
            EngineBox.SelectedItem = profile.Engine;
            _lastEngine = profile.Engine;
            HostBox.Text = profile.RemoteHost;
            PortBox.Text = profile.RemotePort.ToString(CultureInfo.InvariantCulture);
            DatabaseNameBox.Text = profile.DatabaseName ?? string.Empty;
            UsernameBox.Text = profile.Username ?? string.Empty;
            AuthenticationBox.SelectedItem = profile.AuthenticationKind;
            PasswordBox.Clear();
        }
        finally
        {
            _loadingEditor = false;
        }

        UpdatePasswordHint();
    }

    private bool TryBuildSpec(out DatabaseProfileSpec? spec, out string error)
    {
        spec = null;
        error = string.Empty;
        if (EngineBox.SelectedItem is not DatabaseEngineKind engine ||
            AuthenticationBox.SelectedItem is not DatabaseAuthenticationKind authenticationKind)
        {
            error = _localization.Get("Loc.DatabaseProfiles.InvalidSelection");
            return false;
        }

        if (!int.TryParse(PortBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535)
        {
            error = _localization.Get("Loc.DatabaseProfiles.InvalidPort");
            return false;
        }

        spec = new DatabaseProfileSpec(
            NameBox.Text,
            engine,
            HostBox.Text,
            port,
            DatabaseNameBox.Text,
            UsernameBox.Text,
            authenticationKind);
        return true;
    }

    private void UpdatePasswordHint()
    {
        var isPassword = AuthenticationBox?.SelectedItem is DatabaseAuthenticationKind.Password;
        if (PasswordBox is not null)
        {
            PasswordBox.IsEnabled = isPassword;
        }

        if (PasswordHintText is null)
        {
            return;
        }

        PasswordHintText.Text = !isPassword
            ? _localization.Get("Loc.DatabaseProfiles.PasswordDisabledHint")
            : _selectedProfile?.AuthenticationKind == DatabaseAuthenticationKind.Password
                ? _localization.Get("Loc.DatabaseProfiles.PasswordExistingHint")
                : _localization.Get("Loc.DatabaseProfiles.PasswordNewHint");
    }

    private void SetBusy(bool value)
    {
        _busy = value;
        SaveButton.IsEnabled = !value;
        ProfilesGrid.IsEnabled = !value;
    }
}
