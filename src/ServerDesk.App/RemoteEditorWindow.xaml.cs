using System.Windows;
using ServerDesk.Application.RemoteEditing;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class RemoteEditorWindow : Window
{
    private readonly IRemoteFileEditorService _editorService;
    private readonly ServerProfile _profile;
    private readonly RemotePath _path;
    private CancellationTokenSource? _operationCancellation;
    private RemoteEditorDocument? _document;

    public RemoteEditorWindow(
        IRemoteFileEditorService editorService,
        ServerProfile profile,
        RemotePath path)
    {
        _editorService = editorService ?? throw new ArgumentNullException(nameof(editorService));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _path = path;
        InitializeComponent();
        TitleText.Text = $"Editor · {_path.Value}";
        EndpointText.Text = $"{_profile.Username}@{_profile.Host}:{_profile.Port}";
        StatusText.Text = "Loading remote file…";
        DiffText.Text = "No document loaded";
    }

    private async void WindowOnLoaded(object sender, RoutedEventArgs e) => await LoadAsync();

    private void WindowOnClosed(object? sender, EventArgs e) => CancelActiveOperation();

    private async void ReloadOnClick(object sender, RoutedEventArgs e)
    {
        if (_document is not null &&
            RemoteEditorDiff.Calculate(_document.Text, EditedBox.Text).TotalChanges > 0 &&
            MessageBox.Show(
                this,
                "Reloading discards unsaved editor changes. Continue?",
                "Discard changes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await LoadAsync();
    }

    private async void SaveOnClick(object sender, RoutedEventArgs e)
    {
        if (_document is null)
        {
            return;
        }

        using var operation = BeginOperation();
        StatusText.Text = "Saving through atomic SFTP upload…";
        try
        {
            var result = await _editorService.SaveWritableAsync(
                _profile,
                _document,
                EditedBox.Text,
                operation.Token);
            ShowSaveResult(result);
            if (result.IsSuccess)
            {
                await LoadAfterSaveAsync(operation.Token);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled: writable save was cancelled; atomic upload did not commit a partial destination.";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Error: {exception.Message}";
        }
    }

    private async void PrivilegedSaveOnClick(object sender, RoutedEventArgs e)
    {
        if (_document is null)
        {
            return;
        }

        var validation = BuildValidation();
        var validatorText = validation is null
            ? "No validator is configured."
            : $"Validator: {validation.Executable}";
        if (MessageBox.Show(
                this,
                $"Replace privileged file '{_path.Value}'?\n\n{validatorText}\n\nServerDesk will stage content, validate before commit, preserve mode/UID/GID, then atomically replace the live file with sudo -n. The live file is not touched when validation fails.",
                "Confirm privileged atomic save",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        using var operation = BeginOperation();
        StatusText.Text = "Staging and validating privileged edit…";
        try
        {
            var result = await _editorService.SavePrivilegedAsync(
                _profile,
                _document,
                EditedBox.Text,
                validation,
                operation.Token);
            ShowSaveResult(result);
            if (result.IsSuccess)
            {
                await LoadAfterSaveAsync(operation.Token);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled: privileged save stopped before a confirmed live-file commit.";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Error: {exception.Message}";
        }
    }

    private void CancelOnClick(object sender, RoutedEventArgs e) => CancelActiveOperation();

    private void EditedBoxOnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_document is null)
        {
            DiffText.Text = "No document loaded";
            return;
        }

        DiffText.Text = RemoteEditorDiff.Calculate(_document.Text, EditedBox.Text).Summary;
    }

    private async Task LoadAsync()
    {
        using var operation = BeginOperation();
        StatusText.Text = "Loading remote UTF-8 text…";
        try
        {
            var document = await _editorService.LoadAsync(_profile, _path, operation.Token);
            ApplyDocument(document);
            StatusText.Text = "Ready. Original and edited panes are synchronized.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled: load stopped.";
        }
        catch (RemoteFileSystemException exception)
        {
            StatusText.Text = $"{exception.Error.Code}: {exception.Error.Message}";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Error: {exception.Message}";
        }
    }

    private async Task LoadAfterSaveAsync(CancellationToken cancellationToken)
    {
        var document = await _editorService.LoadAsync(_profile, _path, cancellationToken);
        ApplyDocument(document);
    }

    private void ApplyDocument(RemoteEditorDocument document)
    {
        _document = document;
        OriginalBox.Text = document.Text;
        EditedBox.Text = document.Text;
        DiffText.Text = "No changes";
        MetadataText.Text = $"{document.Metadata.Permissions} · UID {document.Metadata.UserId?.ToString() ?? "—"} · GID {document.Metadata.GroupId?.ToString() ?? "—"} · {RemoteExplorerProjection.FormatBytes(document.Metadata.Size)}";
    }

    private RemoteEditValidationSpec? BuildValidation()
    {
        var executable = ValidatorExecutableBox.Text.Trim();
        if (executable.Length == 0)
        {
            return null;
        }

        var arguments = ValidatorArgumentsBox.Text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (!arguments.Any(argument => argument.Contains("{file}", StringComparison.Ordinal)))
        {
            arguments.Add("{file}");
        }

        return new RemoteEditValidationSpec(executable, arguments);
    }

    private void ShowSaveResult(RemoteEditorSaveResult result)
    {
        StatusText.Text = result.IsSuccess
            ? $"Ready: {result.Message}"
            : result.ValidationFailed
                ? $"Validation failed: {result.Message} Live file was not replaced."
                : $"{result.Error?.Code.ToString() ?? "Error"}: {result.Message}";
    }

    private OperationScope BeginOperation()
    {
        CancelActiveOperation();
        _operationCancellation = new CancellationTokenSource();
        return new OperationScope(this, _operationCancellation);
    }

    private void CancelActiveOperation()
    {
        if (_operationCancellation is not null && !_operationCancellation.IsCancellationRequested)
        {
            _operationCancellation.Cancel();
        }
    }

    private sealed class OperationScope : IDisposable
    {
        private readonly RemoteEditorWindow _owner;
        private readonly CancellationTokenSource _source;
        private bool _disposed;

        public OperationScope(RemoteEditorWindow owner, CancellationTokenSource source)
        {
            _owner = owner;
            _source = source;
        }

        public CancellationToken Token => _source.Token;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (ReferenceEquals(_owner._operationCancellation, _source))
            {
                _owner._operationCancellation = null;
            }

            _source.Dispose();
        }
    }
}
