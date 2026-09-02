using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using ServerDesk.App.Localization;
using ServerDesk.App.Presentation;
using ServerDesk.Application.Profiles;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class ProfileMetadataImportWindow : Window
{
    private readonly ProfileMetadataTransferDocument _document;
    private readonly IProfileMetadataTransferService _service;
    private readonly ILocalizationService _localization;
    private CancellationTokenSource? _cancellation;
    private string _statusKey = "Loc.Transfer.ImportReady";
    private object?[] _statusArguments = [];
    private bool _isRunning;

    public ProfileMetadataImportWindow(
        ProfileMetadataTransferDocument document,
        IProfileMetadataTransferService service,
        ILocalizationService localization)
    {
        InitializeComponent();
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        Rows = new ObservableCollection<ProfileMetadataImportRowViewModel>(
            _document.Profiles.Select((entry, index) =>
                new ProfileMetadataImportRowViewModel(index, entry, _localization)));
        DataContext = this;
        Closing += WindowOnClosing;
        _localization.LanguageChanged += LocalizationOnLanguageChanged;
        RenderLocalization();
        UpdateControls();
    }

    public ObservableCollection<ProfileMetadataImportRowViewModel> Rows { get; }

    public bool HasChanges { get; private set; }

    private void WindowOnClosing(object? sender, CancelEventArgs e)
    {
        _cancellation?.Cancel();
        _localization.LanguageChanged -= LocalizationOnLanguageChanged;
        Closing -= WindowOnClosing;
    }

    private void LocalizationOnLanguageChanged()
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(RenderLocalization);
    }

    private void RenderLocalization()
    {
        TargetSummaryText.Text = _localization.Format("Loc.Transfer.ImportTargetSummary", Rows.Count);
        foreach (var row in Rows)
        {
            row.Relocalize();
        }

        RenderStatus();
    }

    private void ReviewedOnClick(object sender, RoutedEventArgs e) => UpdateControls();

    private async void ImportOnClick(object sender, RoutedEventArgs e)
    {
        if (_isRunning || ReviewedCheckBox.IsChecked != true)
        {
            SetStatus("Loc.Transfer.ReviewRequired");
            UpdateControls();
            return;
        }

        foreach (var row in Rows)
        {
            row.Reset();
        }

        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        _isRunning = true;
        SetStatus("Loc.Transfer.ImportRunning", Rows.Count);
        UpdateControls();

        try
        {
            await _service.ImportAsync(
                _document,
                PublishUpdateAsync,
                cancellation.Token).ConfigureAwait(true);

            if (ReferenceEquals(_cancellation, cancellation))
            {
                var imported = Rows.Count(row => row.State == ProfileMetadataImportRowState.Imported);
                var duplicates = Rows.Count(row => row.State == ProfileMetadataImportRowState.Duplicate);
                var failed = Rows.Count(row => row.State == ProfileMetadataImportRowState.Failed);
                var cancelled = Rows.Count(row => row.State == ProfileMetadataImportRowState.Cancelled);
                SetStatus(
                    "Loc.Transfer.ImportComplete",
                    imported,
                    duplicates,
                    failed,
                    cancelled,
                    Rows.Count);
            }
        }
        catch
        {
            if (ReferenceEquals(_cancellation, cancellation))
            {
                SetStatus("Loc.Transfer.ImportUnexpectedFailure");
            }
        }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation))
            {
                _cancellation = null;
                _isRunning = false;
                UpdateControls();
            }

            cancellation.Dispose();
        }
    }

    private ValueTask PublishUpdateAsync(ProfileMetadataImportUpdate update)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return ValueTask.CompletedTask;
        }

        return new ValueTask(Dispatcher.InvokeAsync(() =>
        {
            var row = Rows.FirstOrDefault(item => item.SourceIndex == update.SourceIndex);
            if (row is null)
            {
                return;
            }

            row.Apply(update);
            if (update.State == ProfileMetadataImportState.Imported)
            {
                HasChanges = true;
            }
        }).Task);
    }

    private void CancelOnClick(object sender, RoutedEventArgs e)
    {
        if (_cancellation is null)
        {
            return;
        }

        _cancellation.Cancel();
        SetStatus("Loc.Transfer.ImportCancelling");
        UpdateControls();
    }

    private void CloseOnClick(object sender, RoutedEventArgs e) => Close();

    private void SetStatus(string key, params object?[] arguments)
    {
        _statusKey = key;
        _statusArguments = arguments;
        RenderStatus();
    }

    private void RenderStatus()
    {
        StatusText.Text = _statusArguments.Length == 0
            ? _localization.Get(_statusKey)
            : _localization.Format(_statusKey, _statusArguments);
    }

    private void UpdateControls()
    {
        ReviewedCheckBox.IsEnabled = !_isRunning;
        ImportButton.IsEnabled = !_isRunning && ReviewedCheckBox.IsChecked == true;
        CancelButton.IsEnabled = _isRunning && _cancellation is not null && !_cancellation.IsCancellationRequested;
        CloseButton.IsEnabled = !_isRunning;
    }
}

public enum ProfileMetadataImportRowState
{
    Pending,
    Imported,
    Duplicate,
    Failed,
    Cancelled,
}

public sealed class ProfileMetadataImportRowViewModel : ObservableObject
{
    private readonly ProfileMetadataTransferEntry _entry;
    private readonly ILocalizationService _localization;
    private ProfileMetadataImportRowState _state = ProfileMetadataImportRowState.Pending;

    public ProfileMetadataImportRowViewModel(
        int sourceIndex,
        ProfileMetadataTransferEntry entry,
        ILocalizationService localization)
    {
        SourceIndex = sourceIndex;
        _entry = entry ?? throw new ArgumentNullException(nameof(entry));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
    }

    public int SourceIndex { get; }

    public string Name => _entry.Name;

    public string Endpoint => $"{_entry.Username}@{_entry.Host}:{_entry.Port}";

    public string EnvironmentDisplay => _entry.Environment ?? _localization.Get("Loc.Transfer.Unlabeled");

    public string AuthenticationDisplay => _localization.Get(_entry.AuthenticationKind switch
    {
        ServerAuthenticationKind.PrivateKey => "Loc.Transfer.AuthPrivateKey",
        ServerAuthenticationKind.SshAgent => "Loc.Transfer.AuthSshAgent",
        ServerAuthenticationKind.KeyboardInteractive => "Loc.Transfer.AuthKeyboardInteractive",
        _ => "Loc.Transfer.AuthPassword",
    });

    public ProfileMetadataImportRowState State => _state;

    public string StateDisplay => _localization.Get(_state switch
    {
        ProfileMetadataImportRowState.Imported => "Loc.Transfer.StateImported",
        ProfileMetadataImportRowState.Duplicate => "Loc.Transfer.StateDuplicate",
        ProfileMetadataImportRowState.Failed => "Loc.Transfer.StateFailed",
        ProfileMetadataImportRowState.Cancelled => "Loc.Transfer.StateCancelled",
        _ => "Loc.Transfer.StatePending",
    });

    public void Reset()
    {
        _state = ProfileMetadataImportRowState.Pending;
        Notify();
    }

    public void Apply(ProfileMetadataImportUpdate update)
    {
        if (update.SourceIndex != SourceIndex)
        {
            return;
        }

        _state = update.State switch
        {
            ProfileMetadataImportState.Imported => ProfileMetadataImportRowState.Imported,
            ProfileMetadataImportState.Duplicate => ProfileMetadataImportRowState.Duplicate,
            ProfileMetadataImportState.Failed => ProfileMetadataImportRowState.Failed,
            ProfileMetadataImportState.Cancelled => ProfileMetadataImportRowState.Cancelled,
            _ => _state,
        };
        Notify();
    }

    public void Relocalize()
    {
        OnPropertyChanged(nameof(EnvironmentDisplay));
        OnPropertyChanged(nameof(AuthenticationDisplay));
        OnPropertyChanged(nameof(StateDisplay));
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StateDisplay));
    }
}
