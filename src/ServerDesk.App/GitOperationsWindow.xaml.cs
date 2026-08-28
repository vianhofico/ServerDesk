using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ServerDesk.App.Localization;
using ServerDesk.Application.Git;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class GitOperationsWindow : Window
{
    private readonly IGitOperationsService _service;
    private readonly ServerProfile _profile;
    private readonly bool _initiallyConnected;
    private readonly ILocalizationService _localization;
    private CancellationTokenSource? _operationCancellation;
    private GitRepositorySnapshot? _snapshot;
    private GitPullPreview? _preview;
    private bool _isBusy;

    public GitOperationsWindow(
        IGitOperationsService service,
        ServerProfile profile,
        bool initiallyConnected,
        ILocalizationService localization)
    {
        InitializeComponent();
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _initiallyConnected = initiallyConnected;
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        RefreshLocalizedHeader();
        SetActions();
    }

    private void WindowOnLoaded(object sender, RoutedEventArgs e)
    {
        _localization.LanguageChanged += LocalizationOnLanguageChanged;
        StatusText.Text = _initiallyConnected
            ? _localization.Get("Loc.Git.Ready")
            : _localization.Get("Loc.Git.Disconnected");
        SetActions();
    }

    private void WindowOnClosed(object? sender, EventArgs e)
    {
        _localization.LanguageChanged -= LocalizationOnLanguageChanged;
        CancelCurrentOperation();
        _operationCancellation?.Dispose();
        _operationCancellation = null;
    }

    private async void InspectOnClick(object sender, RoutedEventArgs e)
    {
        if (!CanStartOperation())
        {
            return;
        }

        var path = RepositoryPathBox.Text;
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusText.Text = _localization.Get("Loc.Git.RepositoryRequired");
            return;
        }

        await InspectAsync(path).ConfigureAwait(true);
    }

    private async void DiscoverOnClick(object sender, RoutedEventArgs e)
    {
        if (!CanStartOperation())
        {
            return;
        }

        if (!int.TryParse(DiscoveryDepthBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var depth))
        {
            StatusText.Text = _localization.Get("Loc.Git.InvalidDepth");
            return;
        }

        var cancellationToken = BeginOperation(_localization.Get("Loc.Git.Discovering"));
        try
        {
            var result = await _service.DiscoverAsync(
                _profile,
                DiscoveryRootBox.Text,
                depth,
                cancellationToken).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                ApplyError(result.Error);
                return;
            }

            DiscoveryCombo.ItemsSource = result.RepositoryPaths;
            if (result.RepositoryPaths.Count > 0)
            {
                DiscoveryCombo.SelectedIndex = 0;
            }

            StatusText.Text = result.Warning ?? _localization.Format("Loc.Git.DiscoveredCount", result.RepositoryPaths.Count);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            StatusText.Text = _localization.Format("Loc.Git.InvalidInput", exception.Message);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = _localization.Get("Loc.Git.Cancelled");
        }
        finally
        {
            EndOperation();
        }
    }

    private void DiscoveryComboOnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DiscoveryCombo.SelectedItem is string path)
        {
            RepositoryPathBox.Text = path;
        }
    }

    private async void FetchOnClick(object sender, RoutedEventArgs e)
    {
        if (!CanStartOperation() || _snapshot is null)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            _localization.Format("Loc.Git.FetchConfirm", _snapshot.RepositoryRoot),
            _localization.Get("Loc.Git.Fetch"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
        {
            StatusText.Text = _localization.Get("Loc.Git.CancelledByUser");
            return;
        }

        var cancellationToken = BeginOperation(_localization.Get("Loc.Git.Fetching"));
        try
        {
            var result = await _service.FetchAsync(_profile, _snapshot.RepositoryRoot, cancellationToken).ConfigureAwait(true);
            if (!result.IsSuccess || result.VerifiedSnapshot is null)
            {
                ApplyError(result.Error);
                return;
            }

            ApplySnapshot(result.VerifiedSnapshot);
            StatusText.Text = _localization.Get("Loc.Git.FetchVerified");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = _localization.Get("Loc.Git.CancelledRefreshBeforeRetry");
        }
        finally
        {
            EndOperation();
        }
    }

    private async void PreviewPullOnClick(object sender, RoutedEventArgs e)
    {
        if (!CanStartOperation() || _snapshot is null)
        {
            return;
        }

        var cancellationToken = BeginOperation(_localization.Get("Loc.Git.Previewing"));
        try
        {
            var result = await _service.PreviewPullAsync(
                _profile,
                _snapshot.RepositoryRoot,
                cancellationToken).ConfigureAwait(true);
            if (!result.IsSuccess || result.Preview is null)
            {
                ApplyError(result.Error);
                return;
            }

            ApplyPreview(result.Preview);
            StatusText.Text = result.Preview.CanApply
                ? _localization.Format("Loc.Git.PreviewReady", result.Preview.Behind, result.Preview.Upstream)
                : result.Preview.Message;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = _localization.Get("Loc.Git.Cancelled");
        }
        finally
        {
            EndOperation();
        }
    }

    private async void PullOnClick(object sender, RoutedEventArgs e)
    {
        if (!CanStartOperation() || _preview is not { CanApply: true })
        {
            return;
        }

        var preview = _preview;
        var confirmation = MessageBox.Show(
            _localization.Format(
                "Loc.Git.PullConfirm",
                preview.Branch,
                preview.Upstream,
                preview.Behind,
                ShortRevision(preview.CurrentRevision)),
            _localization.Get("Loc.Git.ApplyPull"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
        {
            StatusText.Text = _localization.Get("Loc.Git.CancelledByUser");
            return;
        }

        var cancellationToken = BeginOperation(_localization.Get("Loc.Git.ApplyingPull"));
        try
        {
            var result = await _service.PullAsync(
                _profile,
                preview.RepositoryRoot,
                preview.CurrentRevision,
                cancellationToken).ConfigureAwait(true);
            if (!result.IsSuccess || result.VerifiedSnapshot is null)
            {
                ApplyError(result.Error);
                if (result.VerifiedSnapshot is not null)
                {
                    ApplySnapshot(result.VerifiedSnapshot);
                }

                return;
            }

            ApplySnapshot(result.VerifiedSnapshot);
            StatusText.Text = _localization.Get("Loc.Git.PullVerified");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = _localization.Get("Loc.Git.CancelledRefreshBeforeRetry");
        }
        finally
        {
            EndOperation();
        }
    }

    private void CancelOnClick(object sender, RoutedEventArgs e)
    {
        if (_operationCancellation is null)
        {
            StatusText.Text = _localization.Get("Loc.Git.NothingToCancel");
            return;
        }

        StatusText.Text = _localization.Get("Loc.Git.CancelRequested");
        CancelCurrentOperation();
    }

    private async Task InspectAsync(string path)
    {
        var cancellationToken = BeginOperation(_localization.Get("Loc.Git.Inspecting"));
        try
        {
            var result = await _service.InspectAsync(_profile, path, cancellationToken).ConfigureAwait(true);
            if (!result.IsSuccess || result.Snapshot is null)
            {
                ApplyError(result.Error);
                return;
            }

            ApplySnapshot(result.Snapshot);
            StatusText.Text = _localization.Get("Loc.Git.InspectVerified");
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            StatusText.Text = _localization.Format("Loc.Git.InvalidInput", exception.Message);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = _localization.Get("Loc.Git.Cancelled");
        }
        finally
        {
            EndOperation();
        }
    }

    private CancellationToken BeginOperation(string status)
    {
        CancelCurrentOperation();
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        _isBusy = true;
        StatusText.Text = status;
        SetActions();
        return _operationCancellation.Token;
    }

    private void EndOperation()
    {
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        _isBusy = false;
        SetActions();
    }

    private bool CanStartOperation() => _initiallyConnected && !_isBusy;

    private void CancelCurrentOperation()
    {
        try
        {
            _operationCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void ApplySnapshot(GitRepositorySnapshot snapshot)
    {
        _snapshot = snapshot;
        _preview = null;
        RepositoryPathBox.Text = snapshot.RepositoryRoot;
        BranchValue.Text = snapshot.IsDetached
            ? _localization.Get("Loc.Git.Detached")
            : snapshot.Branch;
        RevisionValue.Text = ShortRevision(snapshot.Revision);
        RevisionValue.ToolTip = snapshot.Revision;
        UpstreamValue.Text = snapshot.Upstream ?? "—";
        AheadBehindValue.Text = _localization.Format("Loc.Git.AheadBehindValue", snapshot.Ahead, snapshot.Behind);
        WorktreeValue.Text = snapshot.IsClean
            ? _localization.Get("Loc.Git.Clean")
            : _localization.Format(
                "Loc.Git.DirtyCounts",
                snapshot.StagedCount,
                snapshot.UnstagedCount,
                snapshot.UntrackedCount);
        ChangesGrid.ItemsSource = snapshot.Changes;
        RemotesGrid.ItemsSource = snapshot.Remotes;
        DiffSummaryText.Text = _localization.Format(
            "Loc.Git.DiffSummary",
            snapshot.UnstagedDiffSummary,
            snapshot.StagedDiffSummary);
        PreviewText.Text = _localization.Get("Loc.Git.NoPreview");
        IncomingCommitsList.ItemsSource = Array.Empty<string>();
        SetActions();
    }

    private void ApplyPreview(GitPullPreview preview)
    {
        _preview = preview;
        PreviewText.Text = preview.Message;
        IncomingCommitsList.ItemsSource = preview.IncomingCommits;
        SetActions();
    }

    private void ApplyError(RemoteError? error)
    {
        if (error is null)
        {
            StatusText.Text = _localization.Get("Loc.Git.UnknownError");
            return;
        }

        StatusText.Text = error.Code == RemoteErrorCode.AmbiguousState
            ? _localization.Format("Loc.Git.AmbiguousError", error.Message)
            : _localization.Format("Loc.Git.Error", error.Message);
    }

    private void SetActions()
    {
        var available = _initiallyConnected && !_isBusy;
        InspectButton.IsEnabled = available;
        DiscoverButton.IsEnabled = available;
        FetchButton.IsEnabled = available && _snapshot is not null;
        PreviewPullButton.IsEnabled = available && _snapshot is not null;
        PullButton.IsEnabled = available && _preview is { CanApply: true };
        CancelButton.IsEnabled = _isBusy;
    }

    private void LocalizationOnLanguageChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(RefreshLocalizedHeader);
            return;
        }

        RefreshLocalizedHeader();
        if (_snapshot is not null)
        {
            ApplySnapshot(_snapshot);
        }
    }

    private void RefreshLocalizedHeader()
    {
        TitleText.Text = _localization.Format("Loc.Git.TitleFormat", _profile.Name);
        EndpointText.Text = $"{_profile.Username}@{_profile.Host}:{_profile.Port}";
    }

    private static string ShortRevision(string revision) =>
        revision.Length <= 12 ? revision : revision[..12];
}
