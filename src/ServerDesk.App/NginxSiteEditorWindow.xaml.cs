using System.Windows;
using ServerDesk.App.Localization;
using ServerDesk.Application.Nginx;
using ServerDesk.Application.RemoteEditing;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class NginxSiteEditorWindow : Window
{
    private readonly INginxSiteEditingService _service;
    private readonly ILocalizationService _localization;
    private readonly ServerProfile _profile;
    private readonly NginxSiteInfo _site;
    private CancellationTokenSource? _operationCancellation;
    private NginxSiteEditDocument? _document;
    private string _statusKey = "Loc.Nginx.Editor.Loading";
    private object[] _statusArguments = [];
    private string? _technicalStatus;
    private bool _busy;

    public NginxSiteEditorWindow(
        INginxSiteEditingService service,
        ILocalizationService localization,
        ServerProfile profile,
        NginxSiteInfo site)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _site = site ?? throw new ArgumentNullException(nameof(site));
        InitializeComponent();
        RequestedPathText.Text = _site.SourcePath;
        RefreshLocalizedPresentation();
    }

    public bool WasApplied { get; private set; }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        _localization.LanguageChanged += LocalizationOnLanguageChanged;
        await LoadDocumentAsync(confirmDiscard: false).ConfigureAwait(true);
    }

    protected override void OnClosed(EventArgs e)
    {
        _localization.LanguageChanged -= LocalizationOnLanguageChanged;
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        base.OnClosed(e);
    }

    private async void ReloadOnClick(object sender, RoutedEventArgs e) =>
        await LoadDocumentAsync(confirmDiscard: true).ConfigureAwait(true);

    private void CancelOnClick(object sender, RoutedEventArgs e) =>
        _operationCancellation?.Cancel();

    private void RawTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        UpdateDiff();

    private void ApplyFieldsOnClick(object sender, RoutedEventArgs e)
    {
        if (_document is null || _busy)
        {
            return;
        }

        var names = ServerNamesTextBox.Text
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = NginxSimpleSiteEditor.Apply(
            RawTextBox.Text,
            _site.SourceOrdinal,
            new NginxSimpleSitePatch(names, ListenTextBox.Text, ProxyPassTextBox.Text));
        if (!result.IsSuccess)
        {
            SetStatus("Loc.Nginx.Editor.SimpleFailed", result.Message);
            return;
        }

        RawTextBox.Text = result.CandidateText;
        EditorTabs.SelectedItem = RawTab;
        SetStatus("Loc.Nginx.Editor.SimpleApplied");
    }

    private async void ApplyOnClick(object sender, RoutedEventArgs e)
    {
        if (_document is null || _busy)
        {
            return;
        }

        var diff = RemoteEditorDiff.Calculate(_document.Document.Text, RawTextBox.Text);
        if (diff.TotalChanges == 0)
        {
            SetStatus("Loc.Nginx.Editor.Ready");
            return;
        }

        var diffText = _localization.Format("Loc.Nginx.Editor.Diff", diff.ChangedLines, diff.AddedLines, diff.RemovedLines);
        var confirmation = _localization.Format(
            "Loc.Nginx.Editor.ConfirmMessage",
            _document.CanonicalPath.Value,
            diffText);
        if (MessageBox.Show(
                confirmation,
                _localization.Get("Loc.Nginx.Editor.ConfirmTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        BeginOperation("Loc.Nginx.Editor.Applying");
        try
        {
            var result = await _service.ApplyAsync(
                    _profile,
                    _document,
                    RawTextBox.Text,
                    _operationCancellation!.Token)
                .ConfigureAwait(true);
            WasApplied |= result.IsSuccess || result.RolledBack || result.AmbiguousState;
            RenderApplyResult(result);
            if (result.IsSuccess)
            {
                _document = _document with
                {
                    Document = _document.Document with { Text = RawTextBox.Text },
                };
                UpdateDiff();
                SetStatus(
                    "Loc.Nginx.Editor.ApplySuccess",
                    technicalStatus: result.RecoveryBackupPath is null
                        ? null
                        : _localization.Format("Loc.Nginx.Editor.Backup", result.RecoveryBackupPath.Value.Value));
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("Loc.Nginx.Editor.Cancelled");
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task LoadDocumentAsync(bool confirmDiscard)
    {
        if (_busy)
        {
            return;
        }

        if (confirmDiscard && _document is not null)
        {
            var diff = RemoteEditorDiff.Calculate(_document.Document.Text, RawTextBox.Text);
            if (diff.TotalChanges > 0 &&
                MessageBox.Show(
                    _localization.Get("Loc.Nginx.Editor.ReloadConfirmMessage"),
                    _localization.Get("Loc.Nginx.Editor.ReloadConfirmTitle"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                return;
            }
        }

        RemotePath requestedPath;
        try
        {
            requestedPath = RemotePath.Parse(_site.SourcePath);
        }
        catch (ArgumentException exception)
        {
            SetStatus("Loc.Nginx.Editor.LoadFailed", exception.Message);
            return;
        }

        BeginOperation("Loc.Nginx.Editor.Loading");
        try
        {
            var result = await _service.LoadAsync(
                    _profile,
                    requestedPath,
                    _operationCancellation!.Token)
                .ConfigureAwait(true);
            if (!result.IsSuccess || result.Document is null)
            {
                _document = null;
                RawTextBox.Text = string.Empty;
                CanonicalPathText.Text = string.Empty;
                SetStatus(
                    "Loc.Nginx.Editor.LoadFailed",
                    result.Error?.Message ?? _site.SourcePath);
                return;
            }

            _document = result.Document;
            RequestedPathText.Text = _document.RequestedPath.Value;
            CanonicalPathText.Text = _document.CanonicalPath.Value;
            RawTextBox.Text = _document.Document.Text;
            ServerNamesTextBox.Text = string.Join(' ', _site.ServerNames);
            ListenTextBox.Text = _site.ListenEndpoints.Count == 1 ? _site.ListenEndpoints[0] : string.Empty;
            ProxyPassTextBox.Text = _site.ProxyTargets.Count == 1 &&
                !_site.ProxyTargets[0].Contains("***@", StringComparison.Ordinal)
                    ? _site.ProxyTargets[0]
                    : string.Empty;
            SetStatus("Loc.Nginx.Editor.Ready");
            UpdateDiff();
        }
        catch (OperationCanceledException)
        {
            SetStatus("Loc.Nginx.Editor.Cancelled");
        }
        finally
        {
            EndOperation();
        }
    }

    private void RenderApplyResult(NginxSiteApplyResult result)
    {
        var key = result.IsSuccess
            ? "Loc.Nginx.Editor.ApplySuccess"
            : result.ValidationFailed
                ? "Loc.Nginx.Editor.ValidationFailed"
                : result.RolledBack
                    ? "Loc.Nginx.Editor.RolledBack"
                    : result.AmbiguousState
                        ? "Loc.Nginx.Editor.Ambiguous"
                        : "Loc.Nginx.Editor.ApplyFailed";

        var details = result.Error?.Message;
        if (result.RecoveryBackupPath is { } backup)
        {
            var backupText = _localization.Format("Loc.Nginx.Editor.Backup", backup.Value);
            details = string.IsNullOrWhiteSpace(details)
                ? backupText
                : details + Environment.NewLine + backupText;
        }

        SetStatus(key, technicalStatus: details);
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

    private void UpdateDiff()
    {
        if (_document is null)
        {
            DiffText.Text = string.Empty;
            UpdateControlState();
            return;
        }

        var diff = RemoteEditorDiff.Calculate(_document.Document.Text, RawTextBox.Text);
        DiffText.Text = _localization.Format(
            "Loc.Nginx.Editor.Diff",
            diff.ChangedLines,
            diff.AddedLines,
            diff.RemovedLines);
        UpdateControlState();
    }

    private void UpdateControlState()
    {
        var loaded = _document is not null;
        ReloadButton.IsEnabled = loaded && !_busy;
        ApplyButton.IsEnabled = loaded && !_busy;
        ApplyFieldsButton.IsEnabled = loaded && !_busy;
        ServerNamesTextBox.IsEnabled = loaded && !_busy;
        ListenTextBox.IsEnabled = loaded && !_busy;
        ProxyPassTextBox.IsEnabled = loaded && !_busy;
        RawTextBox.IsReadOnly = !loaded || _busy;
        CancelButton.IsEnabled = _busy;
    }

    private void SetStatus(string key, params object[] arguments) =>
        SetStatus(key, arguments, technicalStatus: null);

    private void SetStatus(string key, string? technicalStatus) =>
        SetStatus(key, [], technicalStatus);

    private void SetStatus(string key, object[]? arguments = null, string? technicalStatus = null)
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
            : localized + Environment.NewLine + NginxSensitiveText.RedactUriUserInfo(_technicalStatus);
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
        TitleText.Text = _localization.Format("Loc.Nginx.Editor.Title", _site.DisplayName);
        RenderStatus();
        UpdateDiff();
    }
}
