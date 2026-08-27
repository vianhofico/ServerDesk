using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ServerDesk.Application.Routing;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class ConnectionRouteWindow : Window
{
    private readonly IServerConnectionRouteService _routeService;
    private readonly ServerProfile _targetProfile;
    private readonly IReadOnlyList<ServerProfile> _profiles;
    private ServerConnectionRoute? _loadedRoute;
    private bool _isBusy;

    public ConnectionRouteWindow(
        IServerConnectionRouteService routeService,
        ServerProfile targetProfile,
        IReadOnlyList<ServerProfile> profiles)
    {
        InitializeComponent();
        _routeService = routeService ?? throw new ArgumentNullException(nameof(routeService));
        _targetProfile = targetProfile ?? throw new ArgumentNullException(nameof(targetProfile));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));

        RouteChoices =
        [
            new(ServerConnectionRouteKind.Direct, "Direct"),
            new(ServerConnectionRouteKind.HttpProxy, "HTTP proxy"),
            new(ServerConnectionRouteKind.Socks4Proxy, "SOCKS4 proxy"),
            new(ServerConnectionRouteKind.Socks5Proxy, "SOCKS5 proxy"),
            new(ServerConnectionRouteKind.Bastion, "SSH bastion / jump host"),
        ];
        BastionChoices = _profiles
            .Where(profile => profile.Id != _targetProfile.Id)
            .OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(profile => new BastionChoice(profile.Id, $"{profile.Name} — {profile.Username}@{profile.Host}:{profile.Port}"))
            .ToArray();

        DataContext = this;
        Title = $"Connection route — {_targetProfile.Name}";
        TitleText.Text = $"Connection route · {_targetProfile.Name}";
        Loaded += WindowOnLoaded;
    }

    public IReadOnlyList<RouteKindChoice> RouteChoices { get; }

    public IReadOnlyList<BastionChoice> BastionChoices { get; }

    private async void WindowOnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= WindowOnLoaded;
        await LoadRouteAsync().ConfigureAwait(true);
    }

    private async Task LoadRouteAsync()
    {
        SetBusy(true, "Loading route…");
        try
        {
            _loadedRoute = await _routeService.GetAsync(_targetProfile.Id).ConfigureAwait(true);
            RouteKindComboBox.SelectedItem = RouteChoices.First(choice => choice.Kind == _loadedRoute.Kind);
            ProxyHostTextBox.Text = _loadedRoute.ProxyHost ?? string.Empty;
            ProxyPortTextBox.Text = _loadedRoute.ProxyPort?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            ProxyUsernameTextBox.Text = _loadedRoute.ProxyUsername ?? string.Empty;
            ProxyPasswordBox.Password = string.Empty;
            ReplaceProxyPasswordCheckBox.IsChecked = false;
            ProxySecretHintText.Text = _loadedRoute.ProxyCredentialReference is null
                ? "No proxy password is stored. Enter one only when the proxy requires authentication."
                : "A proxy password is stored in Windows Credential Manager. ServerDesk will not reveal it here.";

            if (_loadedRoute.BastionProfileId is { } bastionId)
            {
                BastionComboBox.SelectedItem = BastionChoices.FirstOrDefault(choice => choice.ProfileId == bastionId);
            }

            UpdateRoutePanels();
            ClearError();
        }
        catch (Exception exception)
        {
            ShowError($"ServerDesk could not load the connection route. {exception.Message}");
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private void RouteKindOnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateRoutePanels();

    private void UpdateRoutePanels()
    {
        if (RouteKindComboBox.SelectedItem is not RouteKindChoice choice)
        {
            ProxyPanel.Visibility = Visibility.Collapsed;
            BastionPanel.Visibility = Visibility.Collapsed;
            RouteDescriptionText.Text = "Select a route type.";
            return;
        }

        var isProxy = choice.Kind is ServerConnectionRouteKind.HttpProxy or
            ServerConnectionRouteKind.Socks4Proxy or
            ServerConnectionRouteKind.Socks5Proxy;
        ProxyPanel.Visibility = isProxy ? Visibility.Visible : Visibility.Collapsed;
        BastionPanel.Visibility = choice.Kind == ServerConnectionRouteKind.Bastion
            ? Visibility.Visible
            : Visibility.Collapsed;
        RouteDescriptionText.Text = choice.Kind switch
        {
            ServerConnectionRouteKind.Direct =>
                "ServerDesk connects directly to the configured SSH host and port.",
            ServerConnectionRouteKind.HttpProxy =>
                "SSH traffic is carried through an HTTP CONNECT proxy using SSH.NET's native proxy transport.",
            ServerConnectionRouteKind.Socks4Proxy =>
                "SSH traffic is carried through a SOCKS4 proxy using SSH.NET's native proxy transport.",
            ServerConnectionRouteKind.Socks5Proxy =>
                "SSH traffic is carried through a SOCKS5 proxy using SSH.NET's native proxy transport.",
            ServerConnectionRouteKind.Bastion =>
                "ServerDesk first establishes a separately trusted SSH session to the bastion, creates a loopback-only tunnel, then verifies and authenticates the target through that tunnel.",
            _ => "Unsupported route type.",
        };
    }

    private async void SaveOnClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy || RouteKindComboBox.SelectedItem is not RouteKindChoice choice)
        {
            return;
        }

        ClearError();
        int? proxyPort = null;
        if (choice.Kind is ServerConnectionRouteKind.HttpProxy or
            ServerConnectionRouteKind.Socks4Proxy or
            ServerConnectionRouteKind.Socks5Proxy)
        {
            if (!int.TryParse(
                    ProxyPortTextBox.Text.Trim(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsedPort))
            {
                ShowError("Proxy port must be a number between 1 and 65535.");
                return;
            }

            proxyPort = parsedPort;
        }

        var bastionId = choice.Kind == ServerConnectionRouteKind.Bastion
            ? (BastionComboBox.SelectedItem as BastionChoice)?.ProfileId
            : null;
        var spec = new ServerConnectionRouteSpec(
            choice.Kind,
            ProxyHostTextBox.Text,
            proxyPort,
            ProxyUsernameTextBox.Text,
            bastionId);
        var password = ProxyPasswordBox.Password;
        var replacePassword = ReplaceProxyPasswordCheckBox.IsChecked == true ||
            (!string.IsNullOrEmpty(password) && _loadedRoute?.ProxyCredentialReference is null);

        SetBusy(true, "Saving route…");
        try
        {
            _loadedRoute = await _routeService.SaveAsync(
                    _targetProfile.Id,
                    spec,
                    password,
                    replacePassword)
                .ConfigureAwait(true);
            ProxyPasswordBox.Password = string.Empty;
            DialogResult = true;
        }
        catch (ServerConnectionRouteValidationException exception)
        {
            ShowError(string.Join(Environment.NewLine, exception.Errors.Values));
        }
        catch (Exception exception)
        {
            ShowError($"ServerDesk could not save the connection route. {exception.Message}");
        }
        finally
        {
            if (IsVisible)
            {
                SetBusy(false, string.Empty);
            }
        }
    }

    private void CancelOnClick(object sender, RoutedEventArgs e)
    {
        ProxyPasswordBox.Password = string.Empty;
        DialogResult = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        ProxyPasswordBox.Password = string.Empty;
        base.OnClosed(e);
    }

    private void SetBusy(bool busy, string status)
    {
        _isBusy = busy;
        StatusText.Text = status;
        RouteKindComboBox.IsEnabled = !busy;
        ProxyPanel.IsEnabled = !busy;
        BastionPanel.IsEnabled = !busy;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void ClearError()
    {
        ErrorText.Text = string.Empty;
        ErrorText.Visibility = Visibility.Collapsed;
    }

    public sealed record RouteKindChoice(ServerConnectionRouteKind Kind, string DisplayName);

    public sealed record BastionChoice(Guid ProfileId, string DisplayName);
}
