using System.Windows;
using System.Windows.Controls;
using ServerDesk.App.Localization;
using ServerDesk.Application.EnvironmentFiles;
using ServerDesk.Application.RemoteEditing;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class EnvironmentFileWindow : Window
{
    private readonly IEnvironmentFileService _service;
    private readonly ILocalizationService _localization;
    private readonly ServerProfile _profile;
    private readonly bool _connected;
    private readonly HashSet<int> _revealedLines = [];
    private CancellationTokenSource? _operationCancellation;
    private EnvironmentFileSnapshot? _snapshot;
    private string _workingText = string.Empty;
    private bool _busy;
    private bool _requiresRefreshAfterAmbiguous;
    private bool _rawRevealed;
    private bool _syncingRaw;
    private string _statusKey = "Loc.Env.PathRequired";
    private object?[] _statusArguments = [];
    private string? _technicalStatus;

    public EnvironmentFileWindow(
        IEnvironmentFileService service,
        ILocalizationService localization,
        ServerProfile profile,
        bool connected)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _connected = connected;
        InitializeComponent();
        RefreshLocalizedPresentation();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        _localization.LanguageChanged += LocalizationOnLanguageChanged;
        if (!_connected)
        {
            SetStatus("Loc.Env.Disconnected");
        }

        UpdateControlState();
    }

    protected override void OnClosed(EventArgs e)
    {
        _localization.LanguageChanged -= LocalizationOnLanguageChanged;
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        base.OnClosed(e);
    }

    private async void LoadOnClick(object sender, RoutedEventArgs e) =>
        await LoadPathAsync(PathTextBox.Text).ConfigureAwait(true);

    private async void RefreshOnClick(object sender, RoutedEventArgs e)
    {
        var path = _snapshot?.Path.Value ?? PathTextBox.Text;
        await LoadPathAsync(path).ConfigureAwait(true);
    }

    private void CancelOnClick(object sender, RoutedEventArgs e) =>
        _operationCancellation?.Cancel();

    private async Task LoadPathAsync(string pathText)
    {
        if (_busy || !_connected)
        {
            return;
        }

        RemotePath path;
        try
        {
            path = RemotePath.Parse(pathText.Trim());
            if (!path.IsAbsolute || path.Value == "/")
            {
                SetStatus("Loc.Env.PathRequired");
                return;
            }
        }
        catch (ArgumentException)
        {
            SetStatus("Loc.Env.PathRequired");
            return;
        }

        BeginOperation("Loc.Env.Loading");
        try
        {
            var result = await _service.LoadAsync(_profile, path, _operationCancellation!.Token).ConfigureAwait(true);
            if (!result.IsSuccess || result.Snapshot is null)
            {
                ClearLoadedState();
                SetStatus("Loc.Env.LoadFailed", technicalStatus: result.Error?.Message);
                return;
            }

            _snapshot = result.Snapshot;
            _workingText = result.Snapshot.Text;
            _requiresRefreshAfterAmbiguous = false;
            _rawRevealed = false;
            _revealedLines.Clear();
            RawPanel.Visibility = Visibility.Collapsed;
            PathTextBox.Text = result.Snapshot.Path.Value;
            ValueTextBox.Clear();
            RebuildRows();
            SetStatus(
                result.Snapshot.Entries.Count == 0 ? "Loc.Env.Empty" : "Loc.Env.Loaded",
                result.Snapshot.Entries.Count == 0 ? [] : [result.Snapshot.Entries.Count]);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Loc.Env.Cancelled");
        }
        finally
        {
            EndOperation();
        }
    }

    private void EntrySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EntryGrid.SelectedItem is EntryRow row)
        {
            KeyTextBox.Text = row.Key;
            ValueTextBox.Clear();
        }

        UpdateControlState();
    }

    private void RevealOnClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _rawRevealed || EntryGrid.SelectedItem is not EntryRow row || !row.Source.IsSecret)
        {
            return;
        }

        if (_revealedLines.Contains(row.LineNumber))
        {
            _revealedLines.Remove(row.LineNumber);
            RebuildRows(row.LineNumber);
            return;
        }

        if (MessageBox.Show(
                _localization.Get("Loc.Env.RevealSecretMessage"),
                _localization.Get("Loc.Env.RevealSecretTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        _revealedLines.Add(row.LineNumber);
        RebuildRows(row.LineNumber);
    }

    private void CopyOnClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _rawRevealed || EntryGrid.SelectedItem is not EntryRow row)
        {
            return;
        }

        if (row.Source.IsSecret &&
            MessageBox.Show(
                _localization.Get("Loc.Env.CopySecretMessage"),
                _localization.Get("Loc.Env.CopySecretTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            Clipboard.SetText(row.Source.Value);
            SetStatus("Loc.Env.Copied");
        }
        catch
        {
            SetStatus("Loc.Env.ApplyFailed");
        }
    }

    private void SetValueOnClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _rawRevealed || EntryGrid.SelectedItem is not EntryRow row)
        {
            return;
        }

        try
        {
            _workingText = EnvironmentFileEditor.SetValueAtLine(
                _workingText,
                row.LineNumber,
                row.Key,
                ValueTextBox.Text);
            WorkingTextChanged("Loc.Env.Updated", row.LineNumber);
        }
        catch (ArgumentException)
        {
            SetStatus("Loc.Env.InvalidEdit");
        }
        catch (InvalidOperationException)
        {
            SetStatus("Loc.Env.InvalidEdit");
        }
    }

    private void AddKeyOnClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _rawRevealed || _snapshot is null)
        {
            return;
        }

        try
        {
            var key = KeyTextBox.Text.Trim();
            EnvironmentFileEditor.ValidateKey(key);
            if (EnvironmentFileParser.Parse(_workingText).Entries.Any(entry =>
                    string.Equals(entry.Key, key, StringComparison.Ordinal)))
            {
                SetStatus("Loc.Env.InvalidEdit");
                return;
            }

            _workingText = EnvironmentFileEditor.AddAssignment(_workingText, key, ValueTextBox.Text);
            WorkingTextChanged("Loc.Env.Added");
            var added = EnvironmentFileParser.Parse(_workingText).Entries.LastOrDefault(entry =>
                string.Equals(entry.Key, key, StringComparison.Ordinal));
            if (added is not null)
            {
                RebuildRows(added.LineNumber);
            }
        }
        catch (ArgumentException)
        {
            SetStatus("Loc.Env.InvalidEdit");
        }
    }

    private void DeleteKeyOnClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _rawRevealed || EntryGrid.SelectedItem is not EntryRow row)
        {
            return;
        }

        if (MessageBox.Show(
                _localization.Format("Loc.Env.DeleteConfirmMessage", row.Key),
                _localization.Get("Loc.Env.DeleteConfirmTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _workingText = EnvironmentFileEditor.DeleteAssignmentAtLine(_workingText, row.LineNumber, row.Key);
            WorkingTextChanged("Loc.Env.Deleted");
        }
        catch (InvalidOperationException)
        {
            SetStatus("Loc.Env.InvalidEdit");
        }
    }

    private void RawAdvancedOnClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _snapshot is null || _rawRevealed)
        {
            return;
        }

        if (MessageBox.Show(
                _localization.Get("Loc.Env.RawWarningMessage"),
                _localization.Get("Loc.Env.RawWarningTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        _rawRevealed = true;
        _revealedLines.Clear();
        _syncingRaw = true;
        RawTextBox.Text = _workingText;
        _syncingRaw = false;
        RawPanel.Visibility = Visibility.Visible;
        SetStatus("Loc.Env.RawEnabled");
        RebuildRows();
        UpdateControlState();
    }

    private void RawTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_rawRevealed || _syncingRaw)
        {
            return;
        }

        _workingText = RawTextBox.Text;
        SetStatus("Loc.Env.WorkingChanged");
        UpdateControlState();
    }

    private async void ApplyOnClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _requiresRefreshAfterAmbiguous || _snapshot is null)
        {
            return;
        }

        if (string.Equals(_snapshot.Text, _workingText, StringComparison.Ordinal))
        {
            SetStatus("Loc.Env.NoChanges");
            return;
        }

        var diff = RemoteEditorDiff.Calculate(_snapshot.Text, _workingText);
        if (MessageBox.Show(
                _localization.Format("Loc.Env.ApplyConfirmMessage", _snapshot.Path.Value, diff.TotalChanges),
                _localization.Get("Loc.Env.ApplyConfirmTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        var validation = BuildValidationSpec();
        if (validation.Invalid)
        {
            SetStatus("Loc.Env.InvalidEdit");
            return;
        }

        BeginOperation("Loc.Env.ApplyRunning");
        EnvironmentFileApplyResult? result = null;
        try
        {
            result = await _service.ApplyAsync(
                    _profile,
                    _snapshot,
                    _workingText,
                    validation.Spec,
                    _operationCancellation!.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Loc.Env.Cancelled");
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
            SetStatus("Loc.Env.Ambiguous", technicalStatus: result.Message);
            UpdateControlState();
            return;
        }

        if (!result.IsSuccess)
        {
            SetStatus(
                result.ValidationFailed ? "Loc.Env.ValidationFailed" : "Loc.Env.ApplyFailed",
                technicalStatus: result.ValidationFailed ? null : result.Message);
            return;
        }

        if (result.Snapshot is not null)
        {
            _snapshot = result.Snapshot;
            _workingText = result.Snapshot.Text;
        }

        _requiresRefreshAfterAmbiguous = false;
        _rawRevealed = false;
        _revealedLines.Clear();
        RawPanel.Visibility = Visibility.Collapsed;
        ValueTextBox.Clear();
        RebuildRows();
        SetStatus("Loc.Env.ApplySuccess");
        UpdateControlState();
    }

    private (EnvironmentFileValidationSpec? Spec, bool Invalid) BuildValidationSpec()
    {
        var executable = ValidatorExecutableTextBox.Text.Trim();
        var rawArguments = ValidatorArgumentsTextBox.Text;
        if (executable.Length == 0 && string.IsNullOrWhiteSpace(rawArguments))
        {
            return (null, false);
        }

        if (executable.Length == 0)
        {
            return (null, true);
        }

        var arguments = rawArguments
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(argument => argument.Trim())
            .Where(argument => argument.Length > 0)
            .ToArray();
        return (new EnvironmentFileValidationSpec(executable, arguments), false);
    }

    private void WorkingTextChanged(string statusKey, int? preferredLine = null)
    {
        _revealedLines.Clear();
        if (_rawRevealed)
        {
            _syncingRaw = true;
            RawTextBox.Text = _workingText;
            _syncingRaw = false;
        }

        ValueTextBox.Clear();
        RebuildRows(preferredLine);
        SetStatus(statusKey);
        UpdateControlState();
    }

    private void RebuildRows(int? preferredLine = null)
    {
        var selectedLine = preferredLine ?? (EntryGrid.SelectedItem as EntryRow)?.LineNumber;
        var parsed = EnvironmentFileParser.Parse(_workingText);
        var rows = parsed.Entries
            .Select(entry => new EntryRow(
                entry,
                entry.LineNumber,
                entry.Key,
                EnvironmentSecretClassifier.DisplayValue(entry, _revealedLines.Contains(entry.LineNumber)),
                _localization.Get(entry.IsSecret ? "Loc.Env.Yes" : "Loc.Env.No")))
            .ToArray();
        EntryGrid.ItemsSource = rows;
        EntryGrid.SelectedItem = rows.FirstOrDefault(row => row.LineNumber == selectedLine);
        UnsupportedWarningText.Visibility = parsed.HasUnsupportedLines ? Visibility.Visible : Visibility.Collapsed;
        UpdateControlState();
    }

    private void ClearLoadedState()
    {
        _snapshot = null;
        _workingText = string.Empty;
        _rawRevealed = false;
        _requiresRefreshAfterAmbiguous = false;
        _revealedLines.Clear();
        EntryGrid.ItemsSource = Array.Empty<EntryRow>();
        RawPanel.Visibility = Visibility.Collapsed;
        KeyTextBox.Clear();
        ValueTextBox.Clear();
        UnsupportedWarningText.Visibility = Visibility.Collapsed;
        UpdateControlState();
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

    private void UpdateControlState()
    {
        LoadButton.IsEnabled = _connected && !_busy;
        RefreshButton.IsEnabled = _connected && !_busy && _snapshot is not null;
        CancelButton.IsEnabled = _busy;
        PathTextBox.IsEnabled = !_busy;
        ValidatorExecutableTextBox.IsEnabled = !_busy;
        ValidatorArgumentsTextBox.IsEnabled = !_busy;
        RawTextBox.IsEnabled = !_busy;

        var selected = EntryGrid.SelectedItem as EntryRow;
        var simpleEnabled = _snapshot is not null && !_busy && !_rawRevealed;
        EntryGrid.IsEnabled = simpleEnabled;
        KeyTextBox.IsEnabled = simpleEnabled;
        ValueTextBox.IsEnabled = simpleEnabled;
        SetValueButton.IsEnabled = simpleEnabled && selected is not null;
        AddKeyButton.IsEnabled = simpleEnabled;
        DeleteKeyButton.IsEnabled = simpleEnabled && selected is not null;
        CopyButton.IsEnabled = simpleEnabled && selected is not null;
        RevealButton.IsEnabled = simpleEnabled && selected?.Source.IsSecret == true;
        RevealButton.Content = _localization.Get(
            selected is not null && _revealedLines.Contains(selected.LineNumber)
                ? "Loc.Env.Hide"
                : "Loc.Env.Reveal");
        RawAdvancedButton.IsEnabled = _snapshot is not null && !_busy && !_rawRevealed;
        ApplyButton.IsEnabled = _snapshot is not null &&
            !_busy &&
            !_requiresRefreshAfterAmbiguous &&
            !string.Equals(_snapshot.Text, _workingText, StringComparison.Ordinal);
    }

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
        TitleText.Text = _localization.Format("Loc.Env.Title", _profile.Name);
        RebuildRows();
        RenderStatus();
        UpdateControlState();
    }

    private sealed record EntryRow(
        EnvironmentFileEntry Source,
        int LineNumber,
        string Key,
        string DisplayValue,
        string SecretLabel);
}
