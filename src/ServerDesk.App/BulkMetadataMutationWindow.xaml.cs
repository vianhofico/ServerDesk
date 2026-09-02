using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ServerDesk.App.Localization;
using ServerDesk.App.Presentation;
using ServerDesk.Application.Profiles;

namespace ServerDesk.App;

public partial class BulkMetadataMutationWindow : Window
{
    private readonly IReadOnlyList<BulkProfileMetadataTarget> _targets;
    private readonly IBulkProfileMetadataMutationService _service;
    private readonly ILocalizationService _localization;
    private CancellationTokenSource? _cancellation;
    private string _statusKey = "Loc.Bulk.Ready";
    private object?[] _statusArguments = [];
    private bool _isRunning;
    private bool _renderingLocalization;

    public BulkMetadataMutationWindow(
        IReadOnlyList<BulkProfileMetadataTarget> targets,
        IBulkProfileMetadataMutationService service,
        ILocalizationService localization)
    {
        InitializeComponent();
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        if (_targets.Count == 0)
        {
            throw new ArgumentException("At least one bulk metadata target is required.", nameof(targets));
        }

        _service = service ?? throw new ArgumentNullException(nameof(service));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        Rows = new ObservableCollection<BulkMetadataTargetRowViewModel>(
            _targets.Select(target => new BulkMetadataTargetRowViewModel(target, _localization)));
        DataContext = this;
        Closing += WindowOnClosing;
        _localization.LanguageChanged += LocalizationOnLanguageChanged;
        RenderLocalization();
        UpdateControls();
    }

    public ObservableCollection<BulkMetadataTargetRowViewModel> Rows { get; }

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
        var selected = GetSelectedOperation() ?? BulkProfileMetadataOperation.AddTag;
        _renderingLocalization = true;
        try
        {
            OperationComboBox.Items.Clear();
            foreach (var operation in Enum.GetValues<BulkProfileMetadataOperation>())
            {
                var item = new ComboBoxItem
                {
                    Tag = operation,
                    Content = GetOperationDisplay(operation),
                };
                OperationComboBox.Items.Add(item);
                if (operation == selected)
                {
                    OperationComboBox.SelectedItem = item;
                }
            }
        }
        finally
        {
            _renderingLocalization = false;
        }

        TargetSummaryText.Text = _localization.Format("Loc.Bulk.TargetSummary", _targets.Count);
        foreach (var row in Rows)
        {
            row.Relocalize();
        }

        RenderOperationSummary();
        RenderStatus();
        UpdateControls();
    }

    private void OperationOnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_renderingLocalization)
        {
            return;
        }

        ReviewedCheckBox.IsChecked = false;
        RenderOperationSummary();
        UpdateControls();
    }

    private void TagOnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_renderingLocalization)
        {
            ReviewedCheckBox.IsChecked = false;
        }

        RenderOperationSummary();
        UpdateControls();
    }

    private void ReviewedOnClick(object sender, RoutedEventArgs e) => UpdateControls();

    private async void ApplyOnClick(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            return;
        }

        var operation = GetSelectedOperation();
        var tag = TagTextBox.Text.Trim();
        if (operation is null || !IsDefinitionValid(operation.Value, tag))
        {
            SetStatus("Loc.Bulk.InvalidDefinition");
            UpdateControls();
            return;
        }

        if (ReviewedCheckBox.IsChecked != true)
        {
            SetStatus("Loc.Bulk.ReviewRequired");
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
        SetStatus("Loc.Bulk.Running", _targets.Count);
        UpdateControls();

        try
        {
            var request = new BulkProfileMetadataRequest(
                _targets,
                operation.Value,
                RequiresTag(operation.Value) ? tag : null);
            await _service.ExecuteAsync(
                request,
                update => PublishUpdateAsync(update),
                cancellation.Token).ConfigureAwait(true);

            if (ReferenceEquals(_cancellation, cancellation))
            {
                var succeeded = Rows.Count(row => row.State == BulkMetadataTargetState.Succeeded);
                var failed = Rows.Count(row => row.State == BulkMetadataTargetState.Failed);
                var cancelled = Rows.Count(row => row.State == BulkMetadataTargetState.Cancelled);
                SetStatus("Loc.Bulk.Complete", succeeded, failed, cancelled, Rows.Count);
            }
        }
        catch
        {
            if (ReferenceEquals(_cancellation, cancellation))
            {
                SetStatus("Loc.Bulk.UnexpectedFailure");
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

    private ValueTask PublishUpdateAsync(BulkProfileMetadataUpdate update)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return ValueTask.CompletedTask;
        }

        return new ValueTask(Dispatcher.InvokeAsync(() =>
        {
            var row = Rows.FirstOrDefault(item => item.ServerProfileId == update.ServerProfileId);
            if (row is null)
            {
                return;
            }

            row.Apply(update);
            if (update.State == BulkProfileMetadataUpdateState.Succeeded)
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
        SetStatus("Loc.Bulk.Cancelling");
        UpdateControls();
    }

    private void CloseOnClick(object sender, RoutedEventArgs e) => Close();

    private BulkProfileMetadataOperation? GetSelectedOperation() =>
        OperationComboBox.SelectedItem is ComboBoxItem { Tag: BulkProfileMetadataOperation operation }
            ? operation
            : null;

    private string GetOperationDisplay(BulkProfileMetadataOperation operation) =>
        _localization.Get(operation switch
        {
            BulkProfileMetadataOperation.AddTag => "Loc.Bulk.OperationAddTag",
            BulkProfileMetadataOperation.RemoveTag => "Loc.Bulk.OperationRemoveTag",
            BulkProfileMetadataOperation.MarkFavorite => "Loc.Bulk.OperationMarkFavorite",
            BulkProfileMetadataOperation.UnmarkFavorite => "Loc.Bulk.OperationUnmarkFavorite",
            _ => "Loc.Bulk.Unknown",
        });

    private void RenderOperationSummary()
    {
        var operation = GetSelectedOperation();
        if (operation is null)
        {
            OperationSummaryText.Text = _localization.Get("Loc.Bulk.InvalidDefinition");
            return;
        }

        var operationDisplay = GetOperationDisplay(operation.Value);
        OperationSummaryText.Text = RequiresTag(operation.Value)
            ? _localization.Format(
                "Loc.Bulk.OperationSummaryTag",
                operationDisplay,
                string.IsNullOrWhiteSpace(TagTextBox.Text) ? "—" : TagTextBox.Text.Trim())
            : _localization.Format("Loc.Bulk.OperationSummary", operationDisplay);
    }

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
        var operation = GetSelectedOperation();
        var definitionValid = operation is not null && IsDefinitionValid(operation.Value, TagTextBox.Text.Trim());
        var hasReview = ReviewedCheckBox.IsChecked == true;
        var requiresTag = operation is not null && RequiresTag(operation.Value);

        TagPanel.Visibility = requiresTag ? Visibility.Visible : Visibility.Collapsed;
        OperationComboBox.IsEnabled = !_isRunning;
        TagTextBox.IsEnabled = !_isRunning;
        ReviewedCheckBox.IsEnabled = !_isRunning;
        ApplyButton.IsEnabled = !_isRunning && definitionValid && hasReview;
        CancelButton.IsEnabled = _isRunning && _cancellation is not null && !_cancellation.IsCancellationRequested;
        CloseButton.IsEnabled = !_isRunning;
    }

    private static bool RequiresTag(BulkProfileMetadataOperation operation) =>
        operation is BulkProfileMetadataOperation.AddTag or BulkProfileMetadataOperation.RemoveTag;

    private static bool IsDefinitionValid(BulkProfileMetadataOperation operation, string tag) =>
        !RequiresTag(operation) ||
        (!string.IsNullOrWhiteSpace(tag) && tag.Length <= 32 && !tag.Contains(',', StringComparison.Ordinal));
}

public enum BulkMetadataTargetState
{
    Pending,
    Applying,
    Succeeded,
    Failed,
    Cancelled,
}

public sealed class BulkMetadataTargetRowViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;
    private BulkMetadataTargetState _state = BulkMetadataTargetState.Pending;

    public BulkMetadataTargetRowViewModel(
        BulkProfileMetadataTarget target,
        ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(target);
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        ServerProfileId = target.ServerProfileId;
        Name = target.Name;
        Endpoint = target.Endpoint;
    }

    public Guid ServerProfileId { get; }

    public string Name { get; }

    public string Endpoint { get; }

    public BulkMetadataTargetState State => _state;

    public string StateDisplay => _localization.Get(_state switch
    {
        BulkMetadataTargetState.Applying => "Loc.Bulk.StateApplying",
        BulkMetadataTargetState.Succeeded => "Loc.Bulk.StateSucceeded",
        BulkMetadataTargetState.Failed => "Loc.Bulk.StateFailed",
        BulkMetadataTargetState.Cancelled => "Loc.Bulk.StateCancelled",
        _ => "Loc.Bulk.StatePending",
    });

    public void Reset()
    {
        _state = BulkMetadataTargetState.Pending;
        Notify();
    }

    public void Apply(BulkProfileMetadataUpdate update)
    {
        if (update.ServerProfileId != ServerProfileId)
        {
            return;
        }

        _state = update.State switch
        {
            BulkProfileMetadataUpdateState.Applying => BulkMetadataTargetState.Applying,
            BulkProfileMetadataUpdateState.Succeeded => BulkMetadataTargetState.Succeeded,
            BulkProfileMetadataUpdateState.Failed => BulkMetadataTargetState.Failed,
            BulkProfileMetadataUpdateState.Cancelled => BulkMetadataTargetState.Cancelled,
            _ => _state,
        };
        Notify();
    }

    public void Relocalize() => OnPropertyChanged(nameof(StateDisplay));

    private void Notify()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StateDisplay));
    }
}
