using System.Windows;
using System.Windows.Controls;
using ServerDesk.App.Localization;
using ServerDesk.Application.Deployment;
using ServerDesk.Application.Docker;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class DeploymentWindow : Window
{
    private readonly IDeploymentOrchestrationService _service;
    private readonly ILocalizationService _localization;
    private readonly ServerProfile _profile;
    private readonly bool _connected;
    private readonly List<DeploymentHealthCheck> _healthChecks = [];
    private CancellationTokenSource? _operationCancellation;
    private DeploymentPlan? _plan;
    private DeploymentRollbackPlan? _rollback;
    private IReadOnlyList<DeploymentStepResult> _resultSteps = [];
    private bool _busy;
    private bool _initializing = true;
    private string _statusKey = "Loc.Deploy.StatusReady";
    private string? _secondaryStatusKey;

    public DeploymentWindow(
        IDeploymentOrchestrationService service,
        ILocalizationService localization,
        ServerProfile profile,
        bool connected)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _connected = connected;
        InitializeComponent();
        RefreshChoices();
        _initializing = false;
        RefreshLocalizedPresentation();
        UpdateTargetPanels();
        UpdateControlState();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        _localization.LanguageChanged += LocalizationOnLanguageChanged;
        if (!_connected)
        {
            SetStatus("Loc.Deploy.StatusDisconnected");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _localization.LanguageChanged -= LocalizationOnLanguageChanged;
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        base.OnClosed(e);
    }

    private void LocalizationOnLanguageChanged()
    {
        _initializing = true;
        try
        {
            RefreshChoices();
            RefreshLocalizedPresentation();
        }
        finally
        {
            _initializing = false;
        }
    }

    private void RefreshChoices()
    {
        var selectedKind = SelectedValue(KindComboBox, DeploymentTargetKind.GitCompose);
        KindComboBox.ItemsSource = new[]
        {
            new Choice<DeploymentTargetKind>(DeploymentTargetKind.GitCompose, _localization.Get("Loc.Deploy.KindGitCompose")),
            new Choice<DeploymentTargetKind>(DeploymentTargetKind.GitSystemd, _localization.Get("Loc.Deploy.KindGitSystemd")),
            new Choice<DeploymentTargetKind>(DeploymentTargetKind.Compose, _localization.Get("Loc.Deploy.KindCompose")),
        };
        SelectValue(KindComboBox, selectedKind);

        var selectedMode = SelectedValue(ComposeModeComboBox, DeploymentComposeMode.Up);
        ComposeModeComboBox.ItemsSource = new[]
        {
            new Choice<DeploymentComposeMode>(DeploymentComposeMode.Up, _localization.Get("Loc.Deploy.ComposeUp")),
            new Choice<DeploymentComposeMode>(DeploymentComposeMode.Restart, _localization.Get("Loc.Deploy.ComposeRestart")),
        };
        SelectValue(ComposeModeComboBox, selectedMode);

        var selectedHealth = SelectedValue(HealthKindComboBox, DeploymentHealthCheckKind.Http);
        HealthKindComboBox.ItemsSource = new[]
        {
            new Choice<DeploymentHealthCheckKind>(DeploymentHealthCheckKind.Http, _localization.Get("Loc.Deploy.HealthHttp")),
            new Choice<DeploymentHealthCheckKind>(DeploymentHealthCheckKind.Tcp, _localization.Get("Loc.Deploy.HealthTcp")),
            new Choice<DeploymentHealthCheckKind>(DeploymentHealthCheckKind.Process, _localization.Get("Loc.Deploy.HealthProcess")),
            new Choice<DeploymentHealthCheckKind>(DeploymentHealthCheckKind.SystemdService, _localization.Get("Loc.Deploy.HealthService")),
            new Choice<DeploymentHealthCheckKind>(DeploymentHealthCheckKind.DockerContainer, _localization.Get("Loc.Deploy.HealthContainer")),
        };
        SelectValue(HealthKindComboBox, selectedHealth);
        UpdateHealthHint();
    }

    private void RefreshLocalizedPresentation()
    {
        HeaderText.Text = _localization.Format("Loc.Deploy.Header", _profile.Name);
        StatusText.Text = _secondaryStatusKey is null
            ? _localization.Get(_statusKey)
            : $"{_localization.Get(_statusKey)} {_localization.Get(_secondaryStatusKey)}";
        HealthGrid.ItemsSource = BuildHealthRows();
        PlanGrid.ItemsSource = _plan?.Steps.Select(BuildPlanRow).ToArray() ?? [];
        ResultGrid.ItemsSource = _resultSteps.Select(BuildResultRow).ToArray();
        UpdateHealthHint();
    }

    private void TargetInputChanged(object sender, TextChangedEventArgs e) => InvalidatePreview();

    private void TargetInputSelectionChanged(object sender, SelectionChangedEventArgs e) => InvalidatePreview();

    private void TargetInputChecked(object sender, RoutedEventArgs e) => InvalidatePreview();

    private void KindSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateTargetPanels();
        InvalidatePreview();
    }

    private void HealthKindSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateHealthHint();
    }

    private void UpdateTargetPanels()
    {
        if (KindComboBox is null)
        {
            return;
        }

        var kind = SelectedValue(KindComboBox, DeploymentTargetKind.GitCompose);
        GitGroup.Visibility = kind is DeploymentTargetKind.GitCompose or DeploymentTargetKind.GitSystemd
            ? Visibility.Visible
            : Visibility.Collapsed;
        ComposeGroup.Visibility = kind is DeploymentTargetKind.GitCompose or DeploymentTargetKind.Compose
            ? Visibility.Visible
            : Visibility.Collapsed;
        SystemdGroup.Visibility = kind == DeploymentTargetKind.GitSystemd
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateHealthHint()
    {
        if (HealthHintText is null || HealthPortTextBox is null)
        {
            return;
        }

        var kind = SelectedValue(HealthKindComboBox, DeploymentHealthCheckKind.Http);
        var key = kind switch
        {
            DeploymentHealthCheckKind.Http => "Loc.Deploy.HealthHintHttp",
            DeploymentHealthCheckKind.Tcp => "Loc.Deploy.HealthHintTcp",
            DeploymentHealthCheckKind.Process => "Loc.Deploy.HealthHintProcess",
            DeploymentHealthCheckKind.SystemdService => "Loc.Deploy.HealthHintService",
            DeploymentHealthCheckKind.DockerContainer => "Loc.Deploy.HealthHintContainer",
            _ => "Loc.Deploy.HealthInvalid",
        };
        HealthHintText.Text = _localization.Get(key);
        HealthPortTextBox.IsEnabled = !_busy && kind == DeploymentHealthCheckKind.Tcp;
    }

    private void AddHealthOnClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        try
        {
            var kind = SelectedValue(HealthKindComboBox, DeploymentHealthCheckKind.Http);
            int? port = null;
            if (!string.IsNullOrWhiteSpace(HealthPortTextBox.Text))
            {
                if (!int.TryParse(HealthPortTextBox.Text, out var parsedPort))
                {
                    throw new FormatException();
                }

                port = parsedPort;
            }

            var normalized = DeploymentTargetPolicy.NormalizeHealthCheck(
                new DeploymentHealthCheck(
                    HealthNameTextBox.Text,
                    kind,
                    HealthTargetTextBox.Text,
                    port));
            if (_healthChecks.Any(check => string.Equals(check.Name, normalized.Name, StringComparison.Ordinal)))
            {
                throw new FormatException();
            }

            _healthChecks.Add(normalized);
            HealthGrid.ItemsSource = BuildHealthRows();
            HealthNameTextBox.Clear();
            HealthTargetTextBox.Clear();
            HealthPortTextBox.Clear();
            InvalidatePreview();
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            SetStatus("Loc.Deploy.HealthInvalid");
        }
    }

    private void RemoveHealthOnClick(object sender, RoutedEventArgs e)
    {
        if (_busy || HealthGrid.SelectedItem is not HealthRow selected)
        {
            return;
        }

        var index = _healthChecks.FindIndex(check =>
            string.Equals(check.Name, selected.Name, StringComparison.Ordinal) &&
            check.Kind == selected.Kind &&
            string.Equals(check.Target, selected.Target, StringComparison.Ordinal) &&
            check.Port == selected.Port);
        if (index >= 0)
        {
            _healthChecks.RemoveAt(index);
            HealthGrid.ItemsSource = BuildHealthRows();
            InvalidatePreview();
        }
    }

    private async void PreviewOnClick(object sender, RoutedEventArgs e)
    {
        if (_busy || !_connected)
        {
            return;
        }

        DeploymentTarget target;
        try
        {
            target = BuildTarget();
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException)
        {
            SetStatus(_healthChecks.Count == 0 ? "Loc.Deploy.HealthRequired" : "Loc.Deploy.InvalidTarget");
            return;
        }

        BeginBusy("Loc.Deploy.StatusPreviewing");
        try
        {
            var result = await _service.PreviewAsync(_profile, target, _operationCancellation!.Token).ConfigureAwait(true);
            if (!result.IsSuccess || result.Plan is null)
            {
                _plan = null;
                _rollback = null;
                SetStatus("Loc.Deploy.StatusError");
                return;
            }

            _plan = result.Plan;
            _rollback = null;
            _resultSteps = [];
            SetStatus(
                "Loc.Deploy.StatusPreviewReady",
                result.Plan.DeterministicRollbackPossible
                    ? "Loc.Deploy.RollbackPotential"
                    : "Loc.Deploy.StatusRollbackUnavailable");
            RefreshLocalizedPresentation();
        }
        catch (OperationCanceledException)
        {
            _plan = null;
            _rollback = null;
            SetStatus("Loc.Deploy.StatusCancelled");
        }
        finally
        {
            EndBusy();
        }
    }

    private async void ExecuteOnClick(object sender, RoutedEventArgs e)
    {
        if (_busy || !_connected || _plan is not { } plan)
        {
            return;
        }

        var mutationCount = plan.Steps.Count(step => step.Risk != OperationRisk.ReadOnly);
        var message = _localization.Format(
            "Loc.Deploy.ExecuteConfirmMessage",
            plan.Steps.Count,
            plan.Target.Id,
            plan.Target.Environment,
            mutationCount);
        if (MessageBox.Show(
                this,
                message,
                _localization.Get("Loc.Deploy.ExecuteConfirmTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        BeginBusy("Loc.Deploy.StatusExecuting");
        try
        {
            var result = await _service.ExecuteAsync(_profile, plan, _operationCancellation!.Token).ConfigureAwait(true);
            _resultSteps = result.Steps;
            _rollback = result.Rollback;
            switch (result.Status)
            {
                case DeploymentRunStatus.Succeeded:
                    SetStatus("Loc.Deploy.StatusSucceeded");
                    break;
                case DeploymentRunStatus.Ambiguous:
                    SetStatus("Loc.Deploy.StatusAmbiguous");
                    break;
                case DeploymentRunStatus.Cancelled:
                    SetStatus("Loc.Deploy.StatusCancelled");
                    break;
                default:
                    SetStatus(
                        "Loc.Deploy.StatusFailed",
                        _rollback is null
                            ? "Loc.Deploy.StatusRollbackUnavailable"
                            : "Loc.Deploy.StatusRollbackAvailable");
                    break;
            }

            RefreshLocalizedPresentation();
        }
        finally
        {
            EndBusy();
        }
    }

    private async void RollbackOnClick(object sender, RoutedEventArgs e)
    {
        if (_busy || !_connected || _rollback is not { } rollback)
        {
            return;
        }

        var message = _localization.Format(
            "Loc.Deploy.RollbackConfirmMessage",
            rollback.TargetId,
            rollback.Environment);
        if (MessageBox.Show(
                this,
                message,
                _localization.Get("Loc.Deploy.RollbackConfirmTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        BeginBusy("Loc.Deploy.StatusRollingBack");
        try
        {
            var result = await _service.RollbackAsync(_profile, rollback, _operationCancellation!.Token).ConfigureAwait(true);
            if (result.Step is not null)
            {
                _resultSteps = [.. _resultSteps, result.Step];
            }

            if (result.IsSuccess)
            {
                _rollback = null;
                _plan = null;
                SetStatus("Loc.Deploy.StatusRollbackSucceeded");
            }
            else if (result.AmbiguousState)
            {
                _rollback = null;
                _plan = null;
                SetStatus("Loc.Deploy.StatusAmbiguous");
            }
            else if (result.Error?.Code == RemoteErrorCode.PathConflict)
            {
                _rollback = null;
                _plan = null;
                SetStatus("Loc.Deploy.StatusRollbackBlocked");
            }
            else
            {
                SetStatus("Loc.Deploy.StatusError");
            }

            RefreshLocalizedPresentation();
        }
        catch (OperationCanceledException)
        {
            _rollback = null;
            _plan = null;
            SetStatus("Loc.Deploy.StatusAmbiguous");
        }
        finally
        {
            EndBusy();
        }
    }

    private void CancelOnClick(object sender, RoutedEventArgs e) => _operationCancellation?.Cancel();

    private DeploymentTarget BuildTarget()
    {
        if (_healthChecks.Count == 0)
        {
            throw new InvalidOperationException();
        }

        var kind = SelectedValue(KindComboBox, DeploymentTargetKind.GitCompose);
        var composeNeeded = kind is DeploymentTargetKind.GitCompose or DeploymentTargetKind.Compose;
        var gitNeeded = kind is DeploymentTargetKind.GitCompose or DeploymentTargetKind.GitSystemd;
        DockerComposeProject? compose = null;
        if (composeNeeded)
        {
            var configs = ComposeConfigsTextBox.Text
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            compose = new DockerComposeProject(ComposeProjectTextBox.Text, string.Empty, configs);
        }

        return new DeploymentTarget(
            TargetIdTextBox.Text,
            TargetNameTextBox.Text,
            EnvironmentTextBox.Text,
            kind,
            gitNeeded ? RepositoryPathTextBox.Text : null,
            compose,
            composeNeeded ? SelectedValue(ComposeModeComboBox, DeploymentComposeMode.Up) : null,
            composeNeeded && ComposePullCheckBox.IsChecked == true,
            composeNeeded && ComposeBuildCheckBox.IsChecked == true,
            kind == DeploymentTargetKind.GitSystemd ? SystemdUnitTextBox.Text : null,
            _healthChecks.ToArray());
    }

    private void InvalidatePreview()
    {
        if (_initializing || _busy)
        {
            return;
        }

        var hadPreview = _plan is not null || _rollback is not null;
        _plan = null;
        _rollback = null;
        _resultSteps = [];
        PlanGrid.ItemsSource = Array.Empty<PlanRow>();
        ResultGrid.ItemsSource = Array.Empty<ResultRow>();
        if (hadPreview)
        {
            SetStatus("Loc.Deploy.StatusPreviewInvalid");
        }

        UpdateControlState();
    }

    private void BeginBusy(string statusKey)
    {
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        _busy = true;
        SetStatus(statusKey);
        UpdateControlState();
    }

    private void EndBusy()
    {
        _busy = false;
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        UpdateControlState();
        UpdateHealthHint();
    }

    private void SetStatus(string key, string? secondaryKey = null)
    {
        _statusKey = key;
        _secondaryStatusKey = secondaryKey;
        StatusText.Text = secondaryKey is null
            ? _localization.Get(key)
            : $"{_localization.Get(key)} {_localization.Get(secondaryKey)}";
        UpdateControlState();
    }

    private void UpdateControlState()
    {
        if (PreviewButton is null)
        {
            return;
        }

        var editable = _connected && !_busy;
        TargetIdTextBox.IsEnabled = editable;
        TargetNameTextBox.IsEnabled = editable;
        EnvironmentTextBox.IsEnabled = editable;
        KindComboBox.IsEnabled = editable;
        RepositoryPathTextBox.IsEnabled = editable;
        ComposeProjectTextBox.IsEnabled = editable;
        ComposeConfigsTextBox.IsEnabled = editable;
        ComposeModeComboBox.IsEnabled = editable;
        ComposePullCheckBox.IsEnabled = editable;
        ComposeBuildCheckBox.IsEnabled = editable;
        SystemdUnitTextBox.IsEnabled = editable;
        HealthNameTextBox.IsEnabled = editable;
        HealthKindComboBox.IsEnabled = editable;
        HealthTargetTextBox.IsEnabled = editable;
        HealthGrid.IsEnabled = editable;
        PreviewButton.IsEnabled = editable;
        ExecuteButton.IsEnabled = editable && _plan is not null;
        RollbackButton.IsEnabled = editable && _rollback is not null;
        CancelButton.IsEnabled = _busy;
        HealthPortTextBox.IsEnabled = editable &&
            SelectedValue(HealthKindComboBox, DeploymentHealthCheckKind.Http) == DeploymentHealthCheckKind.Tcp;
    }

    private IReadOnlyList<HealthRow> BuildHealthRows() =>
        _healthChecks.Select(check =>
            new HealthRow(
                check.Name,
                check.Kind,
                check.Target,
                check.Port,
                LocalizeHealthKind(check.Kind),
                check.Port is { } port ? $"{check.Target}:{port}" : check.Target))
            .ToArray();

    private PlanRow BuildPlanRow(DeploymentPlanStep step) =>
        new(
            step.Sequence,
            LocalizeStep(step),
            LocalizeRisk(step.Risk),
            _localization.Get(step.Conditional ? "Loc.Deploy.Yes" : "Loc.Deploy.No"));

    private ResultRow BuildResultRow(DeploymentStepResult result) =>
        new(
            result.Step.Sequence,
            LocalizeStep(result.Step),
            LocalizeOutcome(result.Outcome),
            _localization.Get(result.Outcome switch
            {
                DeploymentStepOutcome.Succeeded => "Loc.Deploy.ResultSucceeded",
                DeploymentStepOutcome.Skipped => "Loc.Deploy.ResultSkipped",
                DeploymentStepOutcome.Unknown => "Loc.Deploy.ResultUnknown",
                DeploymentStepOutcome.Cancelled => "Loc.Deploy.ResultCancelled",
                _ => "Loc.Deploy.ResultFailed",
            }));

    private string LocalizeStep(DeploymentPlanStep step) => step.Kind switch
    {
        DeploymentStepKind.GitFetch => _localization.Get("Loc.Deploy.StepGitFetch"),
        DeploymentStepKind.GitFastForward => _localization.Get("Loc.Deploy.StepGitFastForward"),
        DeploymentStepKind.ComposePull => _localization.Get("Loc.Deploy.StepComposePull"),
        DeploymentStepKind.ComposeBuild => _localization.Get("Loc.Deploy.StepComposeBuild"),
        DeploymentStepKind.ComposeUp => _localization.Get("Loc.Deploy.StepComposeUp"),
        DeploymentStepKind.ComposeRestart => _localization.Get("Loc.Deploy.StepComposeRestart"),
        DeploymentStepKind.SystemdRestart => _localization.Get("Loc.Deploy.StepSystemdRestart"),
        DeploymentStepKind.HealthCheck => _localization.Format("Loc.Deploy.StepHealth", step.HealthCheckName ?? string.Empty),
        DeploymentStepKind.RollbackComposeDown => _localization.Get("Loc.Deploy.StepRollbackDown"),
        _ => _localization.Get("Loc.Deploy.StatusError"),
    };

    private string LocalizeRisk(OperationRisk risk) => risk switch
    {
        OperationRisk.ReadOnly => _localization.Get("Loc.Deploy.RiskReadOnly"),
        OperationRisk.Mutating => _localization.Get("Loc.Deploy.RiskMutating"),
        OperationRisk.Destructive => _localization.Get("Loc.Deploy.RiskDestructive"),
        _ => _localization.Get("Loc.Deploy.StatusError"),
    };

    private string LocalizeOutcome(DeploymentStepOutcome outcome) => _localization.Get(outcome switch
    {
        DeploymentStepOutcome.Succeeded => "Loc.Deploy.OutcomeSucceeded",
        DeploymentStepOutcome.Failed => "Loc.Deploy.OutcomeFailed",
        DeploymentStepOutcome.Skipped => "Loc.Deploy.OutcomeSkipped",
        DeploymentStepOutcome.Unknown => "Loc.Deploy.OutcomeUnknown",
        DeploymentStepOutcome.Cancelled => "Loc.Deploy.OutcomeCancelled",
        _ => "Loc.Deploy.OutcomeFailed",
    });

    private string LocalizeHealthKind(DeploymentHealthCheckKind kind) => _localization.Get(kind switch
    {
        DeploymentHealthCheckKind.Http => "Loc.Deploy.HealthHttp",
        DeploymentHealthCheckKind.Tcp => "Loc.Deploy.HealthTcp",
        DeploymentHealthCheckKind.Process => "Loc.Deploy.HealthProcess",
        DeploymentHealthCheckKind.SystemdService => "Loc.Deploy.HealthService",
        DeploymentHealthCheckKind.DockerContainer => "Loc.Deploy.HealthContainer",
        _ => "Loc.Deploy.HealthInvalid",
    });

    private static T SelectedValue<T>(ComboBox comboBox, T fallback)
        where T : struct, Enum =>
        comboBox.SelectedItem is Choice<T> selected ? selected.Value : fallback;

    private static void SelectValue<T>(ComboBox comboBox, T value)
        where T : struct, Enum
    {
        if (comboBox.ItemsSource is IEnumerable<Choice<T>> choices)
        {
            comboBox.SelectedItem = choices.FirstOrDefault(choice => EqualityComparer<T>.Default.Equals(choice.Value, value));
        }
    }

    private sealed record Choice<T>(T Value, string Display)
        where T : struct, Enum;

    private sealed record HealthRow(
        string Name,
        DeploymentHealthCheckKind Kind,
        string Target,
        int? Port,
        string KindDisplay,
        string TargetDisplay);

    private sealed record PlanRow(
        int Sequence,
        string StepDisplay,
        string RiskDisplay,
        string ConditionalDisplay);

    private sealed record ResultRow(
        int Sequence,
        string StepDisplay,
        string OutcomeDisplay,
        string Message);
}
