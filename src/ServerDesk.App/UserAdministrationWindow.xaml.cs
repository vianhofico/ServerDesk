using System.Windows;
using System.Windows.Controls;
using ServerDesk.App.Localization;
using ServerDesk.Application.UserAdministration;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class UserAdministrationWindow : Window
{
    private readonly IUserAdministrationService _userService;
    private readonly IAuthorizedKeyAdministrationService _keyService;
    private readonly ILocalizationService _localization;
    private readonly ServerProfile _profile;
    private readonly bool _connected;
    private CancellationTokenSource? _operationCancellation;
    private UserAdministrationSnapshot? _snapshot;
    private AuthorizedKeySnapshot? _keySnapshot;
    private UserMutationPreview? _userPreview;
    private AuthorizedKeyMutationPreview? _keyPreview;
    private bool _busy;
    private bool _updatingEditor;

    public UserAdministrationWindow(
        IUserAdministrationService userService,
        IAuthorizedKeyAdministrationService keyService,
        ILocalizationService localization,
        ServerProfile profile,
        bool connected)
    {
        _userService = userService ?? throw new ArgumentNullException(nameof(userService));
        _keyService = keyService ?? throw new ArgumentNullException(nameof(keyService));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _connected = connected;
        InitializeComponent();
        BuildMutationChoices();
        CreateShellTextBox.Text = "/bin/bash";
        RefreshLocalizedPresentation();
        RefreshControlState();
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        _localization.LanguageChanged += LocalizationOnLanguageChanged;
        if (_connected)
        {
            await RefreshUsersAsync().ConfigureAwait(true);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _localization.LanguageChanged -= LocalizationOnLanguageChanged;
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        base.OnClosed(e);
    }

    private async void RefreshOnClick(object sender, RoutedEventArgs e) =>
        await RefreshUsersAsync().ConfigureAwait(true);

    private void CancelOnClick(object sender, RoutedEventArgs e) =>
        _operationCancellation?.Cancel();

    private void UserSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingEditor)
        {
            return;
        }

        InvalidateUserPreview();
        InvalidateKeyPreview(clearKeys: true);
        if (SelectedUser() is { } user)
        {
            _updatingEditor = true;
            try
            {
                TargetUsernameTextBox.Text = user.Username;
                CreateHomeTextBox.Text = user.Home;
                CreateShellTextBox.Text = user.Shell;
            }
            finally
            {
                _updatingEditor = false;
            }
        }

        RefreshSelectedUserPresentation();
        RefreshControlState();
    }

    private void KeySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingEditor)
        {
            InvalidateKeyPreview(clearKeys: false);
        }
    }

    private void UserEditorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingEditor)
        {
            return;
        }

        InvalidateUserPreview();
        RefreshUserEditorState();
    }

    private void UserEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_updatingEditor)
        {
            InvalidateUserPreview();
        }
    }

    private void UserEditorToggleChanged(object sender, RoutedEventArgs e)
    {
        if (!_updatingEditor)
        {
            InvalidateUserPreview();
        }
    }

    private void KeyEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_updatingEditor)
        {
            InvalidateKeyPreview(clearKeys: false);
        }
    }

    private async void PreviewUserOnClick(object sender, RoutedEventArgs e)
    {
        if (_busy || !_connected)
        {
            return;
        }

        UserMutationRequest request;
        try
        {
            request = BuildUserRequest();
        }
        catch (InvalidOperationException exception)
        {
            StatusText.Text = _localization.Format("Loc.UserAdmin.PreviewError", exception.Message);
            return;
        }

        BeginBusy("Loc.UserAdmin.Previewing");
        try
        {
            var result = await _userService.PreviewAsync(
                _profile,
                request,
                _operationCancellation!.Token).ConfigureAwait(true);
            _userPreview = result.Preview;
            StatusText.Text = result.IsSuccess
                ? _localization.Get("Loc.UserAdmin.PreviewReady")
                : _localization.Format(
                    "Loc.UserAdmin.PreviewError",
                    result.Error?.Message ?? _localization.Get("Loc.UserAdmin.UnknownError"));
            RenderUserPreview();
        }
        catch (OperationCanceledException)
        {
            _userPreview = null;
            StatusText.Text = _localization.Get("Loc.UserAdmin.Cancelled");
            RenderUserPreview();
        }
        finally
        {
            EndBusy();
        }
    }

    private async void ExecuteUserOnClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _userPreview is not { } preview)
        {
            return;
        }

        if (!Confirm(
            _localization.Get("Loc.UserAdmin.ConfirmUserTitle"),
            _localization.Format(
                "Loc.UserAdmin.ConfirmUserMessage",
                RiskDisplay(preview.Risk),
                preview.DisplayCommand,
                preview.ConnectedUserImpact.Message)))
        {
            return;
        }

        var refresh = false;
        BeginBusy("Loc.UserAdmin.Executing");
        try
        {
            var result = await _userService.ExecuteAsync(
                _profile,
                preview,
                _operationCancellation!.Token).ConfigureAwait(true);
            _userPreview = null;
            if (result.IsSuccess)
            {
                StatusText.Text = _localization.Get("Loc.UserAdmin.UserSucceeded");
                refresh = true;
            }
            else if (result.AmbiguousState)
            {
                StatusText.Text = _localization.Get("Loc.UserAdmin.Ambiguous");
            }
            else
            {
                StatusText.Text = _localization.Format("Loc.UserAdmin.MutationFailed", result.Error?.Message ?? result.Message);
            }

            RenderUserPreview();
        }
        catch (OperationCanceledException)
        {
            _userPreview = null;
            StatusText.Text = _localization.Get("Loc.UserAdmin.Cancelled");
            RenderUserPreview();
        }
        finally
        {
            EndBusy();
        }

        if (refresh)
        {
            await RefreshUsersAsync().ConfigureAwait(true);
        }
    }

    private async void LoadKeysOnClick(object sender, RoutedEventArgs e) =>
        await LoadKeysAsync().ConfigureAwait(true);

    private async void PreviewAddKeyOnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedUser() is not { } user || _busy || !_connected)
        {
            return;
        }

        await PreviewKeyAsync(
            user,
            new AuthorizedKeyMutationRequest(
                AuthorizedKeyMutationKind.Add,
                user.Username,
                PublicKeyTextBox.Text)).ConfigureAwait(true);
    }

    private async void PreviewRemoveKeyOnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedUser() is not { } user ||
            KeyGrid.SelectedItem is not AuthorizedPublicKeyInfo key ||
            _busy || !_connected)
        {
            return;
        }

        await PreviewKeyAsync(
            user,
            new AuthorizedKeyMutationRequest(
                AuthorizedKeyMutationKind.Remove,
                user.Username,
                Fingerprint: key.Fingerprint)).ConfigureAwait(true);
    }

    private async void ExecuteKeyOnClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _keyPreview is not { } preview || SelectedUser() is not { } user)
        {
            return;
        }

        if (!Confirm(
            _localization.Get("Loc.UserAdmin.ConfirmKeyTitle"),
            _localization.Format(
                "Loc.UserAdmin.ConfirmKeyMessage",
                RiskDisplay(preview.Risk),
                preview.Summary,
                preview.ConnectedUserImpact.Message)))
        {
            return;
        }

        var reload = false;
        BeginBusy("Loc.UserAdmin.ExecutingKey");
        try
        {
            var result = await _keyService.ExecuteAsync(
                _profile,
                user,
                preview,
                _operationCancellation!.Token).ConfigureAwait(true);
            _keyPreview = null;
            if (result.IsSuccess)
            {
                StatusText.Text = _localization.Get("Loc.UserAdmin.KeySucceeded");
                reload = true;
            }
            else if (result.AmbiguousState)
            {
                StatusText.Text = _localization.Get("Loc.UserAdmin.Ambiguous");
            }
            else
            {
                StatusText.Text = _localization.Format("Loc.UserAdmin.MutationFailed", result.Error?.Message ?? result.Message);
            }

            RenderKeyPreview();
        }
        catch (OperationCanceledException)
        {
            _keyPreview = null;
            StatusText.Text = _localization.Get("Loc.UserAdmin.Cancelled");
            RenderKeyPreview();
        }
        finally
        {
            EndBusy();
        }

        if (reload)
        {
            await LoadKeysAsync().ConfigureAwait(true);
        }
    }

    private async Task RefreshUsersAsync()
    {
        if (_busy || !_connected)
        {
            StatusText.Text = _connected
                ? StatusText.Text
                : _localization.Get("Loc.UserAdmin.Disconnected");
            return;
        }

        var selectedName = SelectedUser()?.Username ?? _profile.Username;
        BeginBusy("Loc.UserAdmin.Loading");
        try
        {
            var result = await _userService.InspectAsync(
                _profile,
                _operationCancellation!.Token).ConfigureAwait(true);
            if (!result.IsSuccess || result.Snapshot is null)
            {
                _snapshot = null;
                UserGrid.ItemsSource = null;
                StatusText.Text = _localization.Format(
                    "Loc.UserAdmin.LoadError",
                    result.Error?.Message ?? _localization.Get("Loc.UserAdmin.UnknownError"));
                return;
            }

            _snapshot = result.Snapshot;
            UserGrid.ItemsSource = _snapshot.Users;
            UserGrid.SelectedItem = _snapshot.Users.FirstOrDefault(item =>
                string.Equals(item.Username, selectedName, StringComparison.Ordinal));
            StatusText.Text = _localization.Format(
                "Loc.UserAdmin.Loaded",
                _snapshot.Users.Count,
                _snapshot.Groups.Count);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = _localization.Get("Loc.UserAdmin.Cancelled");
        }
        finally
        {
            EndBusy();
            RefreshSelectedUserPresentation();
        }
    }

    private async Task LoadKeysAsync()
    {
        if (_busy || !_connected || SelectedUser() is not { } user)
        {
            return;
        }

        BeginBusy("Loc.UserAdmin.LoadingKeys");
        try
        {
            var result = await _keyService.LoadAsync(
                _profile,
                user,
                _operationCancellation!.Token).ConfigureAwait(true);
            if (!result.IsSuccess || result.Snapshot is null)
            {
                _keySnapshot = null;
                KeyGrid.ItemsSource = null;
                StatusText.Text = _localization.Format(
                    "Loc.UserAdmin.KeysError",
                    result.Error?.Message ?? _localization.Get("Loc.UserAdmin.UnknownError"));
                return;
            }

            _keySnapshot = result.Snapshot;
            KeyGrid.ItemsSource = _keySnapshot.Keys;
            StatusText.Text = _localization.Format(
                "Loc.UserAdmin.KeysLoaded",
                _keySnapshot.Keys.Count,
                user.Username);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = _localization.Get("Loc.UserAdmin.Cancelled");
        }
        finally
        {
            EndBusy();
            InvalidateKeyPreview(clearKeys: false);
        }
    }

    private async Task PreviewKeyAsync(LocalUserInfo user, AuthorizedKeyMutationRequest request)
    {
        BeginBusy("Loc.UserAdmin.PreviewingKey");
        try
        {
            var result = await _keyService.PreviewAsync(
                _profile,
                user,
                request,
                _operationCancellation!.Token).ConfigureAwait(true);
            _keyPreview = result.Preview;
            StatusText.Text = result.IsSuccess
                ? _localization.Get("Loc.UserAdmin.KeyPreviewReady")
                : _localization.Format(
                    "Loc.UserAdmin.PreviewError",
                    result.Error?.Message ?? _localization.Get("Loc.UserAdmin.UnknownError"));
            RenderKeyPreview();
        }
        catch (OperationCanceledException)
        {
            _keyPreview = null;
            StatusText.Text = _localization.Get("Loc.UserAdmin.Cancelled");
            RenderKeyPreview();
        }
        finally
        {
            EndBusy();
        }
    }

    private UserMutationRequest BuildUserRequest()
    {
        var kind = SelectedMutationKind();
        if (kind == UserMutationKind.Create)
        {
            var username = TargetUsernameTextBox.Text.Trim();
            return new UserMutationRequest(
                kind,
                username,
                Create: new CreateLocalUserSpec(
                    username,
                    CreateHomeTextBox.Text,
                    CreateShellTextBox.Text,
                    CreateHomeCheckBox.IsChecked == true));
        }

        var user = SelectedUser() ?? throw new InvalidOperationException(
            _localization.Get("Loc.UserAdmin.SelectUserRequired"));
        return kind switch
        {
            UserMutationKind.ChangeShell or UserMutationKind.ChangeHome or
            UserMutationKind.AddGroup or UserMutationKind.RemoveGroup =>
                new UserMutationRequest(kind, user.Username, UserValueTextBox.Text),
            UserMutationKind.Lock or UserMutationKind.Unlock =>
                new UserMutationRequest(kind, user.Username),
            _ => throw new InvalidOperationException(_localization.Get("Loc.UserAdmin.InvalidMutation")),
        };
    }

    private void BuildMutationChoices()
    {
        _updatingEditor = true;
        try
        {
            var selected = SelectedMutationKind();
            UserMutationComboBox.ItemsSource = new[]
            {
                new Choice<UserMutationKind>(UserMutationKind.Create, _localization.Get("Loc.UserAdmin.KindCreate")),
                new Choice<UserMutationKind>(UserMutationKind.ChangeShell, _localization.Get("Loc.UserAdmin.KindShell")),
                new Choice<UserMutationKind>(UserMutationKind.ChangeHome, _localization.Get("Loc.UserAdmin.KindHome")),
                new Choice<UserMutationKind>(UserMutationKind.Lock, _localization.Get("Loc.UserAdmin.KindLock")),
                new Choice<UserMutationKind>(UserMutationKind.Unlock, _localization.Get("Loc.UserAdmin.KindUnlock")),
                new Choice<UserMutationKind>(UserMutationKind.AddGroup, _localization.Get("Loc.UserAdmin.KindAddGroup")),
                new Choice<UserMutationKind>(UserMutationKind.RemoveGroup, _localization.Get("Loc.UserAdmin.KindRemoveGroup")),
            };
            UserMutationComboBox.SelectedValue = selected;
            if (UserMutationComboBox.SelectedItem is null)
            {
                UserMutationComboBox.SelectedValue = UserMutationKind.Lock;
            }
        }
        finally
        {
            _updatingEditor = false;
        }

        RefreshUserEditorState();
    }

    private void RefreshUserEditorState()
    {
        var kind = SelectedMutationKind();
        var create = kind == UserMutationKind.Create;
        var value = kind is UserMutationKind.ChangeShell or UserMutationKind.ChangeHome or
            UserMutationKind.AddGroup or UserMutationKind.RemoveGroup;
        TargetUsernameTextBox.IsEnabled = !_busy && create;
        UserValueTextBox.IsEnabled = !_busy && value;
        CreateHomeTextBox.IsEnabled = !_busy && create;
        CreateShellTextBox.IsEnabled = !_busy && create;
        CreateHomeCheckBox.IsEnabled = !_busy && create;
        RefreshControlState();
    }

    private void RefreshSelectedUserPresentation()
    {
        if (SelectedUser() is not { } user)
        {
            SelectedUserText.Text = _localization.Get("Loc.UserAdmin.NoUserSelected");
            KeysUserText.Text = _localization.Get("Loc.UserAdmin.NoUserSelected");
            return;
        }

        SelectedUserText.Text = _localization.Format(
            "Loc.UserAdmin.SelectedUser",
            user.Username,
            user.UserId,
            user.PrimaryGroup,
            user.SupplementaryGroups.Count == 0 ? "—" : string.Join(", ", user.SupplementaryGroups));
        KeysUserText.Text = _localization.Format("Loc.UserAdmin.KeysFor", user.Username);
    }

    private void RenderUserPreview()
    {
        if (_userPreview is not { } preview)
        {
            UserPreviewTextBox.Text = string.Empty;
            UserImpactText.Text = _localization.Get("Loc.UserAdmin.NoPreview");
            RefreshControlState();
            return;
        }

        UserPreviewTextBox.Text = preview.DisplayCommand;
        UserImpactText.Text = _localization.Format(
            "Loc.UserAdmin.Impact",
            ImpactDisplay(preview.ConnectedUserImpact.Kind),
            preview.ConnectedUserImpact.Message);
        RefreshControlState();
    }

    private void RenderKeyPreview()
    {
        if (_keyPreview is not { } preview)
        {
            KeyPreviewTextBox.Text = string.Empty;
            KeyImpactText.Text = _localization.Get("Loc.UserAdmin.NoPreview");
            RefreshControlState();
            return;
        }

        KeyPreviewTextBox.Text = preview.Summary;
        KeyImpactText.Text = _localization.Format(
            "Loc.UserAdmin.Impact",
            ImpactDisplay(preview.ConnectedUserImpact.Kind),
            preview.ConnectedUserImpact.Message);
        RefreshControlState();
    }

    private void InvalidateUserPreview()
    {
        _userPreview = null;
        RenderUserPreview();
    }

    private void InvalidateKeyPreview(bool clearKeys)
    {
        _keyPreview = null;
        if (clearKeys)
        {
            _keySnapshot = null;
            KeyGrid.ItemsSource = null;
            PublicKeyTextBox.Text = string.Empty;
        }

        RenderKeyPreview();
    }

    private void BeginBusy(string statusKey)
    {
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        _busy = true;
        StatusText.Text = _localization.Get(statusKey);
        RefreshControlState();
    }

    private void EndBusy()
    {
        _busy = false;
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        RefreshUserEditorState();
        RefreshControlState();
    }

    private void RefreshControlState()
    {
        var selectedUser = SelectedUser();
        var safeSelectedUser = selectedUser is not null &&
            !string.Equals(selectedUser.Username, "root", StringComparison.OrdinalIgnoreCase);
        var create = SelectedMutationKind() == UserMutationKind.Create;
        RefreshButton.IsEnabled = _connected && !_busy;
        CancelButton.IsEnabled = _busy;
        PreviewUserButton.IsEnabled = _connected && !_busy && (create || safeSelectedUser);
        ExecuteUserButton.IsEnabled = _connected && !_busy && _userPreview is not null;
        LoadKeysButton.IsEnabled = _connected && !_busy && selectedUser is not null;
        PreviewAddKeyButton.IsEnabled = _connected && !_busy && safeSelectedUser;
        PreviewRemoveKeyButton.IsEnabled = _connected && !_busy && safeSelectedUser && KeyGrid.SelectedItem is AuthorizedPublicKeyInfo;
        ExecuteKeyButton.IsEnabled = _connected && !_busy && _keyPreview is not null;
    }

    private void RefreshLocalizedPresentation()
    {
        TitleText.Text = _localization.Format("Loc.UserAdmin.Title", _profile.Name);
        if (!_connected)
        {
            StatusText.Text = _localization.Get("Loc.UserAdmin.Disconnected");
        }
        else if (_snapshot is null && string.IsNullOrWhiteSpace(StatusText.Text))
        {
            StatusText.Text = _localization.Get("Loc.UserAdmin.Initial");
        }

        BuildMutationChoices();
        RefreshSelectedUserPresentation();
        RenderUserPreview();
        RenderKeyPreview();
    }

    private void LocalizationOnLanguageChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(RefreshLocalizedPresentation);
            return;
        }

        RefreshLocalizedPresentation();
    }

    private bool Confirm(string title, string message) =>
        MessageBox.Show(
            this,
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private LocalUserInfo? SelectedUser() => UserGrid.SelectedItem as LocalUserInfo;

    private UserMutationKind SelectedMutationKind() =>
        UserMutationComboBox.SelectedValue is UserMutationKind kind
            ? kind
            : UserMutationKind.Lock;

    private string RiskDisplay(OperationRisk risk) =>
        _localization.Get(risk == OperationRisk.Destructive
            ? "Loc.UserAdmin.RiskDestructive"
            : "Loc.UserAdmin.RiskMutating");

    private string ImpactDisplay(ConnectedUserImpactKind kind) =>
        _localization.Get(kind switch
        {
            ConnectedUserImpactKind.PossibleRestriction => "Loc.UserAdmin.ImpactPossible",
            ConnectedUserImpactKind.Unknown => "Loc.UserAdmin.ImpactUnknown",
            _ => "Loc.UserAdmin.ImpactNone",
        });

    private sealed record Choice<T>(T Value, string Text)
        where T : struct, Enum;
}
