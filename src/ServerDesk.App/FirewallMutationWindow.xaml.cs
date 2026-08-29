using System.Windows;
using System.Windows.Controls;
using ServerDesk.App.Localization;
using ServerDesk.Application.Firewall;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class FirewallMutationWindow : Window
{
    private readonly IFirewallMutationService _service;
    private readonly ILocalizationService _localization;
    private readonly ServerProfile _profile;
    private readonly FirewallInventorySnapshot? _snapshot;
    private readonly FirewallRuleInfo? _selectedRule;
    private CancellationTokenSource? _operationCancellation;
    private FirewallMutationPreview? _preview;
    private bool _busy;
    private bool _updatingEditor;

    public FirewallMutationWindow(
        IFirewallMutationService service,
        ILocalizationService localization,
        ServerProfile profile,
        FirewallInventorySnapshot? snapshot,
        FirewallRuleInfo? selectedRule)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _snapshot = snapshot;
        _selectedRule = selectedRule;
        InitializeComponent();
        SourceTextBox.Text = "any";
        ZoneTextBox.Text = selectedRule?.Zone ?? "public";
        RebuildChoices();
        RefreshLocalizedPresentation();
        RefreshEditorState();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        _localization.LanguageChanged += LocalizationOnLanguageChanged;
    }

    protected override void OnClosed(EventArgs e)
    {
        _localization.LanguageChanged -= LocalizationOnLanguageChanged;
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        base.OnClosed(e);
    }

    private void EditorOnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingEditor)
        {
            return;
        }

        InvalidatePreview();
        RefreshEditorState();
    }

    private void EditorTextOnChanged(object sender, TextChangedEventArgs e)
    {
        if (!_updatingEditor)
        {
            InvalidatePreview();
        }
    }

    private async void PreviewOnClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        FirewallMutationRequest request;
        try
        {
            request = BuildRequest();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            StatusText.Text = _localization.Format("Loc.FirewallMutation.PreviewError", exception.Message);
            return;
        }

        BeginBusy("Loc.FirewallMutation.Previewing");
        try
        {
            var result = await _service.PreviewAsync(_profile, request, _operationCancellation!.Token).ConfigureAwait(true);
            if (!result.IsSuccess || result.Preview is null)
            {
                _preview = null;
                StatusText.Text = _localization.Format(
                    "Loc.FirewallMutation.PreviewError",
                    result.Error?.Message ?? _localization.Get("Loc.FirewallMutation.UnknownError"));
                RenderPreview();
                return;
            }

            _preview = result.Preview;
            StatusText.Text = _localization.Get("Loc.FirewallMutation.PreviewReady");
            RenderPreview();
        }
        catch (OperationCanceledException)
        {
            _preview = null;
            StatusText.Text = _localization.Get("Loc.FirewallMutation.Cancelled");
            RenderPreview();
        }
        finally
        {
            EndBusy();
        }
    }

    private async void ExecuteOnClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _preview is not { } preview)
        {
            return;
        }

        var risk = _localization.Get(preview.Risk == OperationRisk.Destructive
            ? "Loc.FirewallMutation.RiskDestructive"
            : "Loc.FirewallMutation.RiskMutating");
        var message = _localization.Format(
            "Loc.FirewallMutation.ExecuteConfirmMessage",
            risk,
            preview.DisplayCommand,
            preview.SshImpact.Message);
        if (MessageBox.Show(
                this,
                message,
                _localization.Get("Loc.FirewallMutation.ExecuteConfirmTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        BeginBusy("Loc.FirewallMutation.Executing");
        try
        {
            var result = await _service.ExecuteAsync(_profile, preview, _operationCancellation!.Token).ConfigureAwait(true);
            _preview = null;
            if (result.IsSuccess)
            {
                StatusText.Text = _localization.Get("Loc.FirewallMutation.Succeeded");
            }
            else if (result.AmbiguousState)
            {
                StatusText.Text = _localization.Get("Loc.FirewallMutation.Ambiguous");
            }
            else
            {
                StatusText.Text = _localization.Format(
                    "Loc.FirewallMutation.Failed",
                    result.Error?.Message ?? result.Message);
            }

            RenderPreview();
        }
        catch (OperationCanceledException)
        {
            _preview = null;
            StatusText.Text = _localization.Get("Loc.FirewallMutation.Cancelled");
            RenderPreview();
        }
        finally
        {
            EndBusy();
        }
    }

    private void CancelOnClick(object sender, RoutedEventArgs e) =>
        _operationCancellation?.Cancel();

    private FirewallMutationRequest BuildRequest()
    {
        var kind = Selected(KindComboBox, FirewallMutationKind.AddRule);
        var adapter = Selected(AdapterComboBox, FirewallAdapterKind.Ufw);
        return kind switch
        {
            FirewallMutationKind.AddRule => new FirewallMutationRequest(
                kind,
                adapter,
                Rule: new FirewallRuleDraft(
                    Selected(ActionComboBox, FirewallRuleAction.Allow),
                    Selected(DirectionComboBox, FirewallRuleDirection.Inbound),
                    ProtocolComboBox.SelectedItem as string ?? string.Empty,
                    PortServiceTextBox.Text,
                    SourceTextBox.Text,
                    ZoneTextBox.Text)),
            FirewallMutationKind.RemoveRule when _selectedRule is not null =>
                new FirewallMutationRequest(kind, _selectedRule.Adapter, _selectedRule.Id),
            FirewallMutationKind.RemoveRule => throw new InvalidOperationException(
                _localization.Get("Loc.FirewallMutation.RemoveSelectionRequired")),
            FirewallMutationKind.Enable or FirewallMutationKind.Disable =>
                new FirewallMutationRequest(kind, adapter),
            _ => throw new InvalidOperationException(_localization.Get("Loc.FirewallMutation.InvalidRequest")),
        };
    }

    private void BeginBusy(string statusKey)
    {
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        _busy = true;
        StatusText.Text = _localization.Get(statusKey);
        RefreshButtonStates();
    }

    private void EndBusy()
    {
        _busy = false;
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        RefreshButtonStates();
    }

    private void InvalidatePreview()
    {
        _preview = null;
        if (!_busy)
        {
            StatusText.Text = _localization.Get("Loc.FirewallMutation.Initial");
        }

        RenderPreview();
    }

    private void RenderPreview()
    {
        if (_preview is not { } preview)
        {
            CommandTextBox.Text = string.Empty;
            SshText.Text = _localization.Get("Loc.FirewallMutation.NoPreview");
            ImpactText.Text = string.Empty;
            RefreshButtonStates();
            return;
        }

        CommandTextBox.Text = preview.DisplayCommand;
        SshText.Text = preview.Ssh.IsFullyObserved && preview.Ssh.ClientSource is not null
            ? _localization.Format(
                "Loc.FirewallMutation.SshObserved",
                preview.Ssh.ClientSource,
                preview.Ssh.ServerPort)
            : _localization.Format("Loc.FirewallMutation.SshPartial", preview.Ssh.ServerPort);
        ImpactText.Text = _localization.Format(
            "Loc.FirewallMutation.Impact",
            ImpactDisplay(preview.SshImpact.Kind),
            preview.SshImpact.Message);
        RefreshButtonStates();
    }

    private void RefreshEditorState()
    {
        var kind = Selected(KindComboBox, FirewallMutationKind.AddRule);
        var add = kind == FirewallMutationKind.AddRule;
        var remove = kind == FirewallMutationKind.RemoveRule;
        var adapter = remove && _selectedRule is not null
            ? _selectedRule.Adapter
            : Selected(AdapterComboBox, FirewallAdapterKind.Ufw);

        AdapterComboBox.IsEnabled = !_busy && !remove;
        ActionComboBox.IsEnabled = !_busy && add && adapter == FirewallAdapterKind.Ufw;
        DirectionComboBox.IsEnabled = !_busy && add && adapter == FirewallAdapterKind.Ufw;
        ProtocolComboBox.IsEnabled = !_busy && add;
        PortServiceTextBox.IsEnabled = !_busy && add;
        SourceTextBox.IsEnabled = !_busy && add && adapter == FirewallAdapterKind.Ufw;
        ZoneTextBox.IsEnabled = !_busy && add && adapter == FirewallAdapterKind.Firewalld;

        if (add && adapter == FirewallAdapterKind.Firewalld)
        {
            SelectValue(ActionComboBox, FirewallRuleAction.Allow);
            SelectValue(DirectionComboBox, FirewallRuleDirection.Inbound);
            SourceTextBox.Text = "any";
        }

        SelectedRuleText.Text = _selectedRule is null
            ? _localization.Get("Loc.FirewallMutation.NoSelectedRule")
            : $"{_selectedRule.Id} · {AdapterDisplay(_selectedRule.Adapter)} · {_selectedRule.PortOrService}/{_selectedRule.Protocol}";
        RefreshButtonStates();
    }

    private void RefreshButtonStates()
    {
        PreviewButton.IsEnabled = !_busy;
        ExecuteButton.IsEnabled = !_busy && _preview is not null;
        CancelButton.IsEnabled = _busy;
    }

    private void RebuildChoices()
    {
        _updatingEditor = true;
        try
        {
            var kind = Selected(KindComboBox, FirewallMutationKind.AddRule);
            var adapter = Selected(
                AdapterComboBox,
                _selectedRule?.Adapter ??
                (_snapshot?.ActiveAdapter is FirewallAdapterKind.Ufw or FirewallAdapterKind.Firewalld
                    ? _snapshot.ActiveAdapter
                    : FirewallAdapterKind.Ufw));
            var action = Selected(ActionComboBox, FirewallRuleAction.Allow);
            var direction = Selected(DirectionComboBox, FirewallRuleDirection.Inbound);
            var protocol = ProtocolComboBox.SelectedItem as string ?? "tcp";

            KindComboBox.ItemsSource = new[]
            {
                new Choice<FirewallMutationKind>(FirewallMutationKind.AddRule, _localization.Get("Loc.FirewallMutation.KindAdd")),
                new Choice<FirewallMutationKind>(FirewallMutationKind.RemoveRule, _localization.Get("Loc.FirewallMutation.KindRemove")),
                new Choice<FirewallMutationKind>(FirewallMutationKind.Enable, _localization.Get("Loc.FirewallMutation.KindEnable")),
                new Choice<FirewallMutationKind>(FirewallMutationKind.Disable, _localization.Get("Loc.FirewallMutation.KindDisable")),
            };
            AdapterComboBox.ItemsSource = new[]
            {
                new Choice<FirewallAdapterKind>(FirewallAdapterKind.Ufw, _localization.Get("Loc.Firewall.AdapterUfw")),
                new Choice<FirewallAdapterKind>(FirewallAdapterKind.Firewalld, _localization.Get("Loc.Firewall.AdapterFirewalld")),
            };
            ActionComboBox.ItemsSource = new[]
            {
                new Choice<FirewallRuleAction>(FirewallRuleAction.Allow, _localization.Get("Loc.Firewall.ActionAllow")),
                new Choice<FirewallRuleAction>(FirewallRuleAction.Deny, _localization.Get("Loc.Firewall.ActionDeny")),
                new Choice<FirewallRuleAction>(FirewallRuleAction.Reject, _localization.Get("Loc.Firewall.ActionReject")),
                new Choice<FirewallRuleAction>(FirewallRuleAction.Limit, _localization.Get("Loc.Firewall.ActionLimit")),
            };
            DirectionComboBox.ItemsSource = new[]
            {
                new Choice<FirewallRuleDirection>(FirewallRuleDirection.Inbound, _localization.Get("Loc.Firewall.DirectionInbound")),
                new Choice<FirewallRuleDirection>(FirewallRuleDirection.Outbound, _localization.Get("Loc.Firewall.DirectionOutbound")),
            };
            ProtocolComboBox.ItemsSource = new[] { "tcp", "udp", string.Empty };

            SelectValue(KindComboBox, kind);
            SelectValue(AdapterComboBox, adapter);
            SelectValue(ActionComboBox, action);
            SelectValue(DirectionComboBox, direction);
            ProtocolComboBox.SelectedItem = protocol;
            if (ProtocolComboBox.SelectedItem is null)
            {
                ProtocolComboBox.SelectedItem = "tcp";
            }
        }
        finally
        {
            _updatingEditor = false;
        }
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

    private void RefreshLocalizedPresentation()
    {
        TitleText.Text = _localization.Format("Loc.FirewallMutation.Title", _profile.Name);
        RebuildChoices();
        RefreshEditorState();
        RenderPreview();
    }

    private string ImpactDisplay(FirewallSshImpactKind kind) =>
        _localization.Get(kind switch
        {
            FirewallSshImpactKind.PossibleRestriction => "Loc.FirewallMutation.ImpactPossibleRestriction",
            FirewallSshImpactKind.Unknown => "Loc.FirewallMutation.ImpactUnknown",
            _ => "Loc.FirewallMutation.ImpactNoKnownRestriction",
        });

    private string AdapterDisplay(FirewallAdapterKind adapter) =>
        _localization.Get(adapter == FirewallAdapterKind.Firewalld
            ? "Loc.Firewall.AdapterFirewalld"
            : "Loc.Firewall.AdapterUfw");

    private static T Selected<T>(ComboBox comboBox, T fallback)
        where T : struct, Enum =>
        comboBox.SelectedValue is T value ? value : fallback;

    private static void SelectValue<T>(ComboBox comboBox, T value)
        where T : struct, Enum =>
        comboBox.SelectedValue = value;

    private sealed record Choice<T>(T Value, string Text)
        where T : struct, Enum;
}
