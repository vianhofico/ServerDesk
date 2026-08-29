using System.Windows;
using System.Windows.Controls;
using ServerDesk.App.Localization;
using ServerDesk.Application.Firewall;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class FirewallInventoryWindow : Window
{
    private readonly IFirewallManager _service;
    private readonly ILocalizationService _localization;
    private readonly ServerProfile _profile;
    private readonly bool _initiallyConnected;
    private CancellationTokenSource? _refreshCancellation;
    private FirewallInventorySnapshot? _snapshot;
    private IReadOnlyList<RuleRow> _rows = [];

    public FirewallInventoryWindow(
        IFirewallManager service,
        ILocalizationService localization,
        ServerProfile profile,
        bool connected)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _initiallyConnected = connected;
        InitializeComponent();
        RefreshLocalizedPresentation();
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        _localization.LanguageChanged += LocalizationOnLanguageChanged;
        if (_initiallyConnected)
        {
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _localization.LanguageChanged -= LocalizationOnLanguageChanged;
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        base.OnClosed(e);
    }

    private async void RefreshOnClick(object sender, RoutedEventArgs e) =>
        await RefreshAsync().ConfigureAwait(true);

    private void CancelOnClick(object sender, RoutedEventArgs e) =>
        _refreshCancellation?.Cancel();

    private void SearchOnTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void RuleSelectionChanged(object sender, SelectionChangedEventArgs e) => RenderSelectedRule();

    private async Task RefreshAsync()
    {
        if (!_initiallyConnected)
        {
            StatusText.Text = _localization.Get("Loc.Firewall.Disconnected");
            AdapterSummaryText.Text = string.Empty;
            return;
        }

        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = new CancellationTokenSource();
        RefreshButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        StatusText.Text = _localization.Get("Loc.Firewall.Loading");

        try
        {
            var result = await _service.InspectAsync(_profile, _refreshCancellation.Token).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                _snapshot = null;
                _rows = [];
                RuleGrid.ItemsSource = _rows;
                StatusText.Text = _localization.Format(
                    "Loc.Firewall.Error",
                    result.Error?.Message ?? _localization.Get("Loc.Firewall.ProbeFailed"));
                AdapterSummaryText.Text = string.Empty;
                RenderSelectedRule();
                return;
            }

            _snapshot = result.Snapshot;
            RebuildRows();
            RenderStatus();
            RenderAdapterSummary();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = _localization.Get("Loc.Firewall.Cancelled");
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            RenderSelectedRule();
        }
    }

    private void RebuildRows()
    {
        var selectedId = (RuleGrid.SelectedItem as RuleRow)?.Rule.Id;
        _rows = _snapshot?.Rules
            .Select(rule => new RuleRow(
                rule,
                AdapterDisplay(rule.Adapter),
                ActionDisplay(rule.Action),
                DirectionDisplay(rule.Direction)))
            .ToArray() ?? [];
        ApplyFilter(selectedId);
    }

    private void ApplyFilter(string? preferredId = null)
    {
        var query = SearchTextBox.Text.Trim();
        IEnumerable<RuleRow> filtered = _rows;
        if (query.Length > 0)
        {
            filtered = filtered.Where(row => row.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var materialized = filtered.ToArray();
        RuleGrid.ItemsSource = materialized;
        if (materialized.Length == 0)
        {
            RenderSelectedRule();
            return;
        }

        var preferred = preferredId is null
            ? null
            : materialized.FirstOrDefault(row => string.Equals(row.Rule.Id, preferredId, StringComparison.Ordinal));
        RuleGrid.SelectedItem = preferred ?? materialized[0];
    }

    private void RenderStatus()
    {
        if (_snapshot is null)
        {
            StatusText.Text = _initiallyConnected
                ? _localization.Get("Loc.Firewall.Initial")
                : _localization.Get("Loc.Firewall.Disconnected");
            return;
        }

        StatusText.Text = _snapshot.Status switch
        {
            FirewallRuntimeStatus.Available when _snapshot.Rules.Count == 0 => _localization.Get("Loc.Firewall.Empty"),
            FirewallRuntimeStatus.Available => _localization.Format(
                "Loc.Firewall.Available",
                AdapterDisplay(_snapshot.ActiveAdapter),
                _snapshot.Rules.Count),
            FirewallRuntimeStatus.Disabled => _localization.Get("Loc.Firewall.Disabled"),
            FirewallRuntimeStatus.CliUnavailable => _localization.Get("Loc.Firewall.CliUnavailable"),
            FirewallRuntimeStatus.PermissionDenied => _localization.Get("Loc.Firewall.PermissionDenied"),
            FirewallRuntimeStatus.AdapterConflict => _localization.Get("Loc.Firewall.AdapterConflict"),
            FirewallRuntimeStatus.ProbeFailed => _localization.Get("Loc.Firewall.ProbeFailed"),
            _ => _localization.Get("Loc.Firewall.ProbeFailed"),
        };
    }

    private void RenderAdapterSummary()
    {
        if (_snapshot is null)
        {
            AdapterSummaryText.Text = string.Empty;
            return;
        }

        var parts = _snapshot.Adapters.Select(observation =>
        {
            var version = string.IsNullOrWhiteSpace(observation.Version)
                ? string.Empty
                : $" {observation.Version}";
            var state = AdapterStateDisplay(observation);
            return $"{AdapterDisplay(observation.Adapter)}{version}: {state}";
        });
        AdapterSummaryText.Text = _localization.Format("Loc.Firewall.AdapterSummary", string.Join(" · ", parts));
    }

    private void RenderSelectedRule()
    {
        if (RuleGrid.SelectedItem is not RuleRow row)
        {
            SelectedTitleText.Text = _localization.Get("Loc.Firewall.SelectRule");
            AdapterZoneText.Text = string.Empty;
            ActionDirectionText.Text = string.Empty;
            ProtocolTargetText.Text = string.Empty;
            SourceDestinationText.Text = string.Empty;
            RawRuleTextBox.Text = string.Empty;
            return;
        }

        SelectedTitleText.Text = row.Rule.Id;
        AdapterZoneText.Text = $"{_localization.Get("Loc.Firewall.Adapter")}: {row.Adapter} · {_localization.Get("Loc.Firewall.Zone")}: {Display(row.Rule.Zone)}";
        ActionDirectionText.Text = $"{_localization.Get("Loc.Firewall.Action")}: {row.Action} · {_localization.Get("Loc.Firewall.Direction")}: {row.Direction}";
        ProtocolTargetText.Text = $"{_localization.Get("Loc.Firewall.Protocol")}: {Display(row.Rule.Protocol)} · {_localization.Get("Loc.Firewall.PortService")}: {Display(row.Rule.PortOrService)}";
        SourceDestinationText.Text = $"{_localization.Get("Loc.Firewall.Source")}: {Display(row.Rule.Source)} · {_localization.Get("Loc.Firewall.Destination")}: {Display(row.Rule.Destination)}";
        RawRuleTextBox.Text = row.Rule.Raw;
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
        TitleText.Text = _localization.Format("Loc.Firewall.Title", _profile.Name);
        RebuildRows();
        RenderStatus();
        RenderAdapterSummary();
        RenderSelectedRule();
    }

    private string AdapterStateDisplay(FirewallAdapterObservation observation)
    {
        if (!observation.CliAvailable)
        {
            return _localization.Get("Loc.Firewall.AdapterStateUnavailable");
        }

        if (observation.PermissionDenied)
        {
            return _localization.Get("Loc.Firewall.AdapterStatePermission");
        }

        if (observation.Detail.StartsWith("probe-failed:", StringComparison.Ordinal))
        {
            return _localization.Get("Loc.Firewall.AdapterStateProbeFailed");
        }

        return _localization.Get(observation.IsActive
            ? "Loc.Firewall.AdapterStateActive"
            : "Loc.Firewall.AdapterStateInactive");
    }

    private string AdapterDisplay(FirewallAdapterKind adapter) =>
        _localization.Get(adapter switch
        {
            FirewallAdapterKind.Ufw => "Loc.Firewall.AdapterUfw",
            FirewallAdapterKind.Firewalld => "Loc.Firewall.AdapterFirewalld",
            _ => "Loc.Firewall.AdapterNone",
        });

    private string ActionDisplay(FirewallRuleAction action) =>
        _localization.Get(action switch
        {
            FirewallRuleAction.Allow => "Loc.Firewall.ActionAllow",
            FirewallRuleAction.Deny => "Loc.Firewall.ActionDeny",
            FirewallRuleAction.Reject => "Loc.Firewall.ActionReject",
            FirewallRuleAction.Limit => "Loc.Firewall.ActionLimit",
            _ => "Loc.Firewall.ActionUnknown",
        });

    private string DirectionDisplay(FirewallRuleDirection direction) =>
        _localization.Get(direction switch
        {
            FirewallRuleDirection.Inbound => "Loc.Firewall.DirectionInbound",
            FirewallRuleDirection.Outbound => "Loc.Firewall.DirectionOutbound",
            FirewallRuleDirection.Any => "Loc.Firewall.DirectionAny",
            _ => "Loc.Firewall.DirectionUnknown",
        });

    private static string Display(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value;

    private sealed record RuleRow(
        FirewallRuleInfo Rule,
        string Adapter,
        string Action,
        string Direction)
    {
        public string Zone => Display(Rule.Zone);
        public string Protocol => Display(Rule.Protocol);
        public string PortOrService => Display(Rule.PortOrService);
        public string Source => Display(Rule.Source);

        public string SearchText => string.Join(
            "\n",
            Rule.Id,
            Adapter,
            Zone,
            Action,
            Direction,
            Protocol,
            PortOrService,
            Source,
            Rule.Destination,
            Rule.Raw);
    }
}
