using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ServerDesk.App.Localization;
using ServerDesk.Application.Nginx;
using ServerDesk.Application.Tls;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class TlsCertificateWindow : Window
{
    private readonly ITlsCertificateService _service;
    private readonly INginxInventoryService _nginxService;
    private readonly ILocalizationService _localization;
    private readonly ServerProfile _profile;
    private readonly bool _connected;
    private CancellationTokenSource? _operationCancellation;
    private TlsCertificateInventorySnapshot? _snapshot;
    private NginxInventorySnapshot? _nginxSnapshot;
    private bool _busy;
    private bool _requiresRefreshAfterAmbiguous;
    private string _statusKey = "Loc.Tls.Initial";
    private object?[] _statusArguments = [];
    private string? _technicalStatus;

    public TlsCertificateWindow(
        ITlsCertificateService service,
        INginxInventoryService nginxService,
        ILocalizationService localization,
        ServerProfile profile,
        bool connected)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _nginxService = nginxService ?? throw new ArgumentNullException(nameof(nginxService));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _connected = connected;
        InitializeComponent();
        RefreshLocalizedPresentation();
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        _localization.LanguageChanged += LocalizationOnLanguageChanged;
        if (!_connected)
        {
            SetStatus("Loc.Tls.Disconnected");
            UpdateControlState();
            return;
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    protected override void OnClosed(EventArgs e)
    {
        _localization.LanguageChanged -= LocalizationOnLanguageChanged;
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        base.OnClosed(e);
    }

    private async void RefreshOnClick(object sender, RoutedEventArgs e) =>
        await RefreshAsync().ConfigureAwait(true);

    private void CancelOnClick(object sender, RoutedEventArgs e) =>
        _operationCancellation?.Cancel();

    private async Task RefreshAsync()
    {
        if (_busy || !_connected)
        {
            return;
        }

        BeginOperation("Loc.Tls.Loading");
        try
        {
            var result = await _service.InspectAsync(_profile, _operationCancellation!.Token).ConfigureAwait(true);
            if (!result.IsSuccess || result.Snapshot is null)
            {
                _snapshot = null;
                _nginxSnapshot = null;
                RebuildCertificateRows();
                RebuildSiteChoices();
                RenderCertbotStatus();
                SetStatus("Loc.Tls.LoadFailed", technicalStatus: result.Error?.Message);
                return;
            }

            _snapshot = result.Snapshot;
            var nginx = await _nginxService.InspectAsync(_profile, _operationCancellation!.Token).ConfigureAwait(true);
            _nginxSnapshot = nginx.IsSuccess ? nginx.Snapshot : null;
            _requiresRefreshAfterAmbiguous = false;
            RebuildCertificateRows();
            RebuildSiteChoices();
            RenderCertbotStatus();
            SetStatus(
                _snapshot.Certificates.Count == 0 ? "Loc.Tls.Empty" : "Loc.Tls.Loaded",
                _snapshot.Certificates.Count == 0 ? [] : [_snapshot.Certificates.Count],
                technicalStatus: nginx.Error?.Message);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Loc.Tls.Cancelled");
        }
        finally
        {
            EndOperation();
        }
    }

    private void CertificateSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RenderSelectedCertificate();
        UpdateControlState();
    }

    private void SiteSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SiteComboBox.SelectedItem is not SiteChoice site)
        {
            UpdateControlState();
            return;
        }

        DomainsTextBox.Text = site.DomainText;
        if (string.IsNullOrWhiteSpace(CertificateNameTextBox.Text))
        {
            CertificateNameTextBox.Text = site.Domains.FirstOrDefault() ?? string.Empty;
        }

        UpdateControlState();
    }

    private void ObtainInputChanged(object sender, RoutedEventArgs e) => UpdateControlState();

    private async void RenewOnClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _requiresRefreshAfterAmbiguous || CertificateGrid.SelectedItem is not CertificateRow row)
        {
            return;
        }

        var certificate = row.Source;
        if (string.IsNullOrWhiteSpace(certificate.CertbotCertificateName))
        {
            SetStatus("Loc.Tls.SelectCertificate");
            return;
        }

        var confirmation = _localization.Format(
            "Loc.Tls.RenewConfirmMessage",
            certificate.CertbotCertificateName,
            certificate.CertificatePath);
        if (MessageBox.Show(
                confirmation,
                _localization.Get("Loc.Tls.RenewConfirmTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        BeginOperation("Loc.Tls.Renewing");
        CertbotMutationResult? result = null;
        try
        {
            result = await _service.RenewAsync(
                    _profile,
                    certificate.CertbotCertificateName,
                    certificate.CertificatePath,
                    _operationCancellation!.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Loc.Tls.Cancelled");
        }
        finally
        {
            EndOperation();
        }

        if (result is null)
        {
            return;
        }

        if (result.AmbiguousState)
        {
            _requiresRefreshAfterAmbiguous = true;
            SetStatus("Loc.Tls.Ambiguous", technicalStatus: result.Error?.Message ?? result.Message);
            UpdateControlState();
            return;
        }

        if (!result.IsSuccess)
        {
            SetStatus("Loc.Tls.RenewFailed", technicalStatus: result.Error?.Message ?? result.Message);
            return;
        }

        await RefreshAsync().ConfigureAwait(true);
        SetStatus("Loc.Tls.RenewSuccess", technicalStatus: result.Message);
    }

    private async void ObtainOnClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _requiresRefreshAfterAmbiguous || SiteComboBox.SelectedItem is not SiteChoice site)
        {
            return;
        }

        var domains = DomainsTextBox.Text
            .Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var certificateName = CertificateNameTextBox.Text.Trim();
        var confirmation = _localization.Format(
            "Loc.Tls.ObtainConfirmMessage",
            site.DisplayName,
            certificateName,
            string.Join(", ", domains));
        if (MessageBox.Show(
                confirmation,
                _localization.Get("Loc.Tls.ObtainConfirmTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        var request = new CertbotObtainRequest(
            site.Id,
            certificateName,
            domains,
            EmailTextBox.Text.Trim(),
            TermsCheckBox.IsChecked == true);

        BeginOperation("Loc.Tls.Obtaining");
        CertbotMutationResult? result = null;
        try
        {
            result = await _service.ObtainAsync(_profile, request, _operationCancellation!.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Loc.Tls.Cancelled");
        }
        finally
        {
            EndOperation();
        }

        if (result is null)
        {
            return;
        }

        if (result.AmbiguousState)
        {
            _requiresRefreshAfterAmbiguous = true;
            SetStatus("Loc.Tls.Ambiguous", technicalStatus: result.Error?.Message ?? result.Message);
            UpdateControlState();
            return;
        }

        if (!result.IsSuccess)
        {
            SetStatus("Loc.Tls.ObtainFailed", technicalStatus: result.Error?.Message ?? result.Message);
            return;
        }

        await RefreshAsync().ConfigureAwait(true);
        SetStatus("Loc.Tls.ObtainSuccess", technicalStatus: result.Message);
    }

    private void BeginOperation(string statusKey)
    {
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        _busy = true;
        SetStatus(statusKey);
        UpdateControlState();
    }

    private void EndOperation()
    {
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        _busy = false;
        UpdateControlState();
    }

    private void RebuildCertificateRows()
    {
        var selectedPath = (CertificateGrid.SelectedItem as CertificateRow)?.CertificatePath;
        var rows = _snapshot?.Certificates
            .Select(certificate => new CertificateRow(
                certificate,
                certificate.CertificatePath,
                HealthLabel(certificate.Health),
                certificate.DaysRemaining?.ToString(CultureInfo.CurrentCulture) ?? "—",
                _localization.Get(certificate.IsCertbotManaged ? "Loc.Tls.Yes" : "Loc.Tls.No")))
            .ToArray() ?? [];
        CertificateGrid.ItemsSource = rows;
        CertificateGrid.SelectedItem = rows.FirstOrDefault(row =>
            string.Equals(row.CertificatePath, selectedPath, StringComparison.Ordinal));
        if (CertificateGrid.SelectedItem is null && rows.Length > 0)
        {
            CertificateGrid.SelectedIndex = 0;
        }

        RenderSelectedCertificate();
    }

    private void RebuildSiteChoices()
    {
        var selectedId = (SiteComboBox.SelectedItem as SiteChoice)?.Id;
        var sites = _nginxSnapshot?.Sites
            .Where(site => site.ServerNames.Count > 0)
            .Select(site => new SiteChoice(
                site.Id,
                site.DisplayName,
                string.Join(' ', site.ServerNames),
                site.ServerNames))
            .OrderBy(site => site.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        SiteComboBox.ItemsSource = sites;
        SiteComboBox.SelectedItem = sites.FirstOrDefault(site => string.Equals(site.Id, selectedId, StringComparison.Ordinal));
        if (SiteComboBox.SelectedItem is null && sites.Length > 0)
        {
            SiteComboBox.SelectedIndex = 0;
        }
    }

    private void RenderSelectedCertificate()
    {
        if (CertificateGrid.SelectedItem is not CertificateRow row)
        {
            SelectedCertificateTitle.Text = _localization.Get("Loc.Tls.SelectCertificate");
            SubjectText.Text = string.Empty;
            IssuerText.Text = string.Empty;
            SansText.Text = string.Empty;
            ExpiryText.Text = string.Empty;
            SitesText.Text = string.Empty;
            return;
        }

        var certificate = row.Source;
        SelectedCertificateTitle.Text = certificate.CertificatePath;
        SubjectText.Text = certificate.Subject ?? certificate.ReadError ?? "—";
        IssuerText.Text = certificate.Issuer ?? "—";
        SansText.Text = certificate.SubjectAlternativeNames.Count == 0
            ? "—"
            : string.Join(", ", certificate.SubjectAlternativeNames);
        ExpiryText.Text = certificate.NotAfterUtc?.ToUniversalTime().ToString("u", CultureInfo.CurrentCulture) ?? "—";
        SitesText.Text = certificate.ReferencedSites.Count == 0
            ? "—"
            : string.Join(", ", certificate.ReferencedSites);
    }

    private void RenderCertbotStatus()
    {
        if (_snapshot is null)
        {
            CertbotStatusText.Text = string.Empty;
            return;
        }

        var certbot = _snapshot.Certbot;
        if (certbot.State == CertbotRuntimeState.Available)
        {
            CertbotStatusText.Text = _localization.Format(
                "Loc.Tls.CertbotAvailable",
                string.IsNullOrWhiteSpace(certbot.Version) ? "?" : certbot.Version,
                _localization.Get(certbot.NginxPluginAvailable ? "Loc.Tls.Yes" : "Loc.Tls.No"),
                certbot.ManagedCertificates.Count);
            return;
        }

        CertbotStatusText.Text = _localization.Format("Loc.Tls.CertbotUnavailable", certbot.State);
    }

    private void UpdateControlState()
    {
        RefreshButton.IsEnabled = _connected && !_busy;
        CancelButton.IsEnabled = _busy;
        var capability = _snapshot?.Certbot;
        var selectedCertificate = (CertificateGrid.SelectedItem as CertificateRow)?.Source;
        RenewButton.IsEnabled = !_busy &&
            !_requiresRefreshAfterAmbiguous &&
            capability?.CanMutate == true &&
            selectedCertificate?.IsCertbotManaged == true;
        ObtainButton.IsEnabled = !_busy &&
            !_requiresRefreshAfterAmbiguous &&
            capability?.CanMutate == true &&
            capability.NginxPluginAvailable &&
            SiteComboBox.SelectedItem is SiteChoice &&
            TermsCheckBox.IsChecked == true;
        SiteComboBox.IsEnabled = !_busy;
        CertificateNameTextBox.IsEnabled = !_busy;
        DomainsTextBox.IsEnabled = !_busy;
        EmailTextBox.IsEnabled = !_busy;
        TermsCheckBox.IsEnabled = !_busy;

        if (_requiresRefreshAfterAmbiguous && !_busy)
        {
            SetStatus("Loc.Tls.RefreshRequired");
        }
    }

    private string HealthLabel(TlsCertificateHealth health) =>
        _localization.Get(health switch
        {
            TlsCertificateHealth.Valid => "Loc.Tls.Health.Valid",
            TlsCertificateHealth.ExpiringSoon => "Loc.Tls.Health.ExpiringSoon",
            TlsCertificateHealth.Expired => "Loc.Tls.Health.Expired",
            TlsCertificateHealth.NotYetValid => "Loc.Tls.Health.NotYetValid",
            _ => "Loc.Tls.Health.Unreadable",
        });

    private void SetStatus(string key, params object?[] arguments) =>
        SetStatus(key, arguments, technicalStatus: null);

    private void SetStatus(string key, string? technicalStatus) =>
        SetStatus(key, [], technicalStatus);

    private void SetStatus(string key, object?[]? arguments = null, string? technicalStatus = null)
    {
        _statusKey = key;
        _statusArguments = arguments ?? [];
        _technicalStatus = technicalStatus;
        RenderStatus();
    }

    private void RenderStatus()
    {
        var localized = _statusArguments.Length == 0
            ? _localization.Get(_statusKey)
            : _localization.Format(_statusKey, _statusArguments);
        StatusText.Text = string.IsNullOrWhiteSpace(_technicalStatus)
            ? localized
            : localized + Environment.NewLine + _technicalStatus;
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
        TitleText.Text = _localization.Format("Loc.Tls.Title", _profile.Name);
        RebuildCertificateRows();
        RenderCertbotStatus();
        RenderStatus();
        UpdateControlState();
    }

    private sealed record CertificateRow(
        TlsCertificateInfo Source,
        string CertificatePath,
        string HealthLabel,
        string DaysLabel,
        string ManagedLabel);

    private sealed record SiteChoice(
        string Id,
        string DisplayName,
        string DomainText,
        IReadOnlyList<string> Domains);
}
