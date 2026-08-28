using System.Windows;
using System.Windows.Controls;
using ServerDesk.App.Localization;
using ServerDesk.Application.Nginx;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class NginxInventoryWindow : Window
{
    private readonly INginxInventoryService _service;
    private readonly ILocalizationService _localization;
    private readonly ServerProfile _profile;
    private readonly bool _initiallyConnected;
    private CancellationTokenSource? _refreshCancellation;
    private NginxInventorySnapshot? _snapshot;
    private IReadOnlyList<SiteRow> _rows = [];

    public NginxInventoryWindow(
        INginxInventoryService service,
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

    private void SiteSelectionChanged(object sender, SelectionChangedEventArgs e) => RenderSelectedSite();

    private async Task RefreshAsync()
    {
        if (!_initiallyConnected)
        {
            StatusText.Text = _localization.Get("Loc.Nginx.Disconnected");
            return;
        }

        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = new CancellationTokenSource();
        RefreshButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        StatusText.Text = _localization.Get("Loc.Nginx.Loading");

        try
        {
            var result = await _service.InspectAsync(_profile, _refreshCancellation.Token).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                _snapshot = null;
                _rows = [];
                SiteGrid.ItemsSource = _rows;
                StatusText.Text = _localization.Format("Loc.Nginx.Error", result.Error?.Message ?? "Unknown error");
                RenderSelectedSite();
                return;
            }

            _snapshot = result.Snapshot;
            _rows = result.Snapshot!.Sites.Select(site => new SiteRow(site)).ToArray();
            ApplyFilter();
            RenderStatus();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = _localization.Get("Loc.Nginx.Cancelled");
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
        }
    }

    private void ApplyFilter()
    {
        var query = SearchTextBox.Text.Trim();
        IEnumerable<SiteRow> filtered = _rows;
        if (query.Length > 0)
        {
            filtered = filtered.Where(row =>
                row.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        SiteGrid.ItemsSource = filtered.ToArray();
        if (SiteGrid.Items.Count > 0 && SiteGrid.SelectedItem is null)
        {
            SiteGrid.SelectedIndex = 0;
        }
        else if (SiteGrid.Items.Count == 0)
        {
            RenderSelectedSite();
        }
    }

    private void RenderStatus()
    {
        if (_snapshot is null)
        {
            StatusText.Text = _initiallyConnected
                ? _localization.Get("Loc.Nginx.Initial")
                : _localization.Get("Loc.Nginx.Disconnected");
            return;
        }

        StatusText.Text = _snapshot.RuntimeState switch
        {
            NginxRuntimeState.Available when _snapshot.Sites.Count == 0 => _localization.Get("Loc.Nginx.Empty"),
            NginxRuntimeState.Available => _localization.Format(
                "Loc.Nginx.Available",
                string.IsNullOrWhiteSpace(_snapshot.Version) ? "?" : _snapshot.Version,
                _snapshot.Sites.Count,
                _snapshot.Sources.Count),
            NginxRuntimeState.CliMissing => _localization.Get("Loc.Nginx.CliMissing"),
            NginxRuntimeState.PermissionDenied => _localization.Get("Loc.Nginx.PermissionDenied"),
            NginxRuntimeState.InvalidConfiguration => _localization.Get("Loc.Nginx.InvalidConfiguration"),
            _ => _localization.Get("Loc.Nginx.ProbeFailed"),
        };

        if (!string.IsNullOrWhiteSpace(_snapshot.RuntimeDetail) &&
            _snapshot.RuntimeState != NginxRuntimeState.Available)
        {
            StatusText.Text += Environment.NewLine + NginxSensitiveText.RedactUriUserInfo(_snapshot.RuntimeDetail);
        }
    }

    private void RenderSelectedSite()
    {
        if (SiteGrid.SelectedItem is not SiteRow row)
        {
            SelectedTitleText.Text = _localization.Get("Loc.Nginx.SelectSite");
            ServerNamesText.Text = string.Empty;
            ProxyTargetsText.Text = string.Empty;
            TlsText.Text = string.Empty;
            CertificatesText.Text = string.Empty;
            RawBlockTextBox.Text = string.Empty;
            return;
        }

        var site = row.Site;
        SelectedTitleText.Text = site.DisplayName;
        ServerNamesText.Text = $"{_localization.Get("Loc.Nginx.ServerNames")}: {Join(site.ServerNames)}";
        ProxyTargetsText.Text = $"{_localization.Get("Loc.Nginx.ProxyTargets")}: {Join(site.ProxyTargets)}";
        TlsText.Text = $"{_localization.Get("Loc.Nginx.Tls")}: {_localization.Get(site.UsesTls ? "Loc.Nginx.Yes" : "Loc.Nginx.No")}";
        CertificatesText.Text = $"{_localization.Get("Loc.Nginx.Certificates")}: {Join(site.CertificatePaths)}";
        RawBlockTextBox.Text = site.PresentationRawBlock;
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
        TitleText.Text = _localization.Format("Loc.Nginx.Title", _profile.Name);
        RenderStatus();
        RenderSelectedSite();
    }

    private static string Join(IReadOnlyList<string> values) =>
        values.Count == 0 ? "—" : string.Join(", ", values);

    private sealed record SiteRow(NginxSiteInfo Site)
    {
        public string DisplayName => Site.DisplayName;
        public string SourcePath => Site.SourcePath;
        public string ListenDisplay => Join(Site.ListenEndpoints);
        public string SearchText => string.Join(
            "\n",
            Site.DisplayName,
            Site.SourcePath,
            string.Join(" ", Site.ServerNames),
            string.Join(" ", Site.ListenEndpoints),
            string.Join(" ", Site.ProxyTargets));
    }
}
