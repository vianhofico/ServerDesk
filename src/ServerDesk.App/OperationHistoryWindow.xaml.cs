using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ServerDesk.App.Localization;
using ServerDesk.Application.Audit;
using ServerDesk.Application.Databases;
using ServerDesk.Application.Profiles;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class OperationHistoryWindow : Window
{
    private readonly IOperationHistoryService _historyService;
    private readonly IServerProfileService _profileService;
    private readonly ILocalizationService _localization;
    private readonly Guid? _initialServerProfileId;
    private readonly ObservableCollection<OperationHistoryRow> _rows = [];
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyList<ServerProfile> _profiles = [];
    private IReadOnlyList<OperationHistoryItem> _lastItems = [];
    private bool _busy;

    public OperationHistoryWindow(
        IOperationHistoryService historyService,
        IServerProfileService profileService,
        ILocalizationService localization,
        Guid? initialServerProfileId = null)
    {
        _historyService = historyService ?? throw new ArgumentNullException(nameof(historyService));
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _initialServerProfileId = initialServerProfileId;

        InitializeComponent();
        HistoryGrid.ItemsSource = _rows;
        LimitComboBox.ItemsSource = new[] { 100, 250, 500 };
        LimitComboBox.SelectedItem = 250;
        _localization.LanguageChanged += LocalizationOnLanguageChanged;
        Loaded += WindowLoaded;
        Closed += WindowClosed;
        ApplyLocalizedState();
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _profiles = await _profileService.ListAsync(_lifetime.Token);
            ApplyLocalizedState();
            await LoadHistoryAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        _localization.LanguageChanged -= LocalizationOnLanguageChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private void LocalizationOnLanguageChanged()
    {
        ApplyLocalizedState();
        RebuildRows();
    }

    private void ApplyLocalizedState()
    {
        var selectedServerId = (ServerComboBox.SelectedItem as ServerFilterOption)?.Id ?? _initialServerProfileId;
        var selectedRisk = (RiskComboBox.SelectedItem as RiskFilterOption)?.Value;
        var selectedOutcome = (OutcomeComboBox.SelectedItem as OutcomeFilterOption)?.Value;
        var selectedEngine = (DatabaseEngineComboBox.SelectedItem as DatabaseEngineFilterOption)?.Value;

        ServerComboBox.ItemsSource = new[]
        {
            new ServerFilterOption(null, _localization.Get("Loc.OperationHistory.AllServers")),
        }.Concat(_profiles.Select(profile => new ServerFilterOption(profile.Id, profile.Name))).ToArray();
        ServerComboBox.SelectedItem = ServerComboBox.Items
            .Cast<ServerFilterOption>()
            .FirstOrDefault(option => option.Id == selectedServerId)
            ?? ServerComboBox.Items[0];

        RiskComboBox.ItemsSource = new[]
        {
            new RiskFilterOption(null, _localization.Get("Loc.OperationHistory.AllRisks")),
            new RiskFilterOption(OperationRisk.ReadOnly, _localization.Get("Loc.OperationHistory.RiskReadOnly")),
            new RiskFilterOption(OperationRisk.Mutating, _localization.Get("Loc.OperationHistory.RiskMutating")),
            new RiskFilterOption(OperationRisk.Destructive, _localization.Get("Loc.OperationHistory.RiskDestructive")),
        };
        RiskComboBox.SelectedItem = RiskComboBox.Items
            .Cast<RiskFilterOption>()
            .FirstOrDefault(option => option.Value == selectedRisk)
            ?? RiskComboBox.Items[0];

        OutcomeComboBox.ItemsSource = new[]
        {
            new OutcomeFilterOption(null, _localization.Get("Loc.OperationHistory.AllOutcomes")),
            new OutcomeFilterOption(OperationOutcome.Succeeded, _localization.Get("Loc.OperationHistory.OutcomeSucceeded")),
            new OutcomeFilterOption(OperationOutcome.Failed, _localization.Get("Loc.OperationHistory.OutcomeFailed")),
            new OutcomeFilterOption(OperationOutcome.Cancelled, _localization.Get("Loc.OperationHistory.OutcomeCancelled")),
            new OutcomeFilterOption(OperationOutcome.Unknown, _localization.Get("Loc.OperationHistory.OutcomeUnknown")),
        };
        OutcomeComboBox.SelectedItem = OutcomeComboBox.Items
            .Cast<OutcomeFilterOption>()
            .FirstOrDefault(option => option.Value == selectedOutcome)
            ?? OutcomeComboBox.Items[0];

        DatabaseEngineComboBox.ItemsSource = new[]
        {
            new DatabaseEngineFilterOption(null, _localization.Get("Loc.OperationHistory.AllDatabaseEngines")),
            new DatabaseEngineFilterOption(DatabaseEngineKind.PostgreSql, "PostgreSQL"),
            new DatabaseEngineFilterOption(DatabaseEngineKind.MySql, "MySQL"),
            new DatabaseEngineFilterOption(DatabaseEngineKind.MariaDb, "MariaDB"),
            new DatabaseEngineFilterOption(DatabaseEngineKind.Redis, "Redis"),
            new DatabaseEngineFilterOption(DatabaseEngineKind.SqlServer, "Microsoft SQL Server"),
        };
        DatabaseEngineComboBox.SelectedItem = DatabaseEngineComboBox.Items
            .Cast<DatabaseEngineFilterOption>()
            .FirstOrDefault(option => option.Value == selectedEngine)
            ?? DatabaseEngineComboBox.Items[0];

        CertificationText.Text = BuildCertificationSummary();
        if (string.IsNullOrWhiteSpace(StatusText.Text))
        {
            StatusText.Text = _localization.Get("Loc.OperationHistory.Ready");
        }

        UpdateDetails();
    }

    private async void LoadOnClick(object sender, RoutedEventArgs e) => await LoadHistoryAsync();

    private async void ClearFiltersOnClick(object sender, RoutedEventArgs e)
    {
        SearchTextBox.Clear();
        CategoryTextBox.Clear();
        FromDatePicker.SelectedDate = null;
        ToDatePicker.SelectedDate = null;
        ServerComboBox.SelectedIndex = 0;
        RiskComboBox.SelectedIndex = 0;
        OutcomeComboBox.SelectedIndex = 0;
        DatabaseEngineComboBox.SelectedIndex = 0;
        DatabaseOnlyCheckBox.IsChecked = false;
        LimitComboBox.SelectedItem = 250;
        await LoadHistoryAsync();
    }

    private async Task LoadHistoryAsync()
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);
        try
        {
            var query = new OperationAuditQuery(
                FromUtc: ToStartOfDayUtc(FromDatePicker.SelectedDate),
                ToUtc: ToEndOfDayUtc(ToDatePicker.SelectedDate),
                ServerProfileId: (ServerComboBox.SelectedItem as ServerFilterOption)?.Id,
                Category: CategoryTextBox.Text,
                Risk: (RiskComboBox.SelectedItem as RiskFilterOption)?.Value,
                Outcome: (OutcomeComboBox.SelectedItem as OutcomeFilterOption)?.Value,
                SearchText: SearchTextBox.Text,
                Limit: LimitComboBox.SelectedItem is int limit ? limit : 250,
                DatabaseOnly: DatabaseOnlyCheckBox.IsChecked == true,
                DatabaseEngine: (DatabaseEngineComboBox.SelectedItem as DatabaseEngineFilterOption)?.Value);
            var result = await _historyService.QueryAsync(query, _lifetime.Token);
            _lastItems = result.Items;
            RebuildRows();
            StatusText.Text = _localization.Format(
                "Loc.OperationHistory.LoadedStatus",
                result.Items.Count,
                result.AppliedLimit);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RebuildRows()
    {
        var selectedId = (HistoryGrid.SelectedItem as OperationHistoryRow)?.Id;
        var names = _profiles.ToDictionary(profile => profile.Id, profile => profile.Name);
        _rows.Clear();
        foreach (var item in _lastItems)
        {
            var entry = item.Entry;
            var serverName = item.ServerProfileId is { } serverId && names.TryGetValue(serverId, out var name)
                ? name
                : item.ServerProfileId is not null
                    ? item.ServerProfileId.Value.ToString("D")
                    : _localization.Get("Loc.OperationHistory.LegacyServer");
            var database = item.DatabaseContext;
            _rows.Add(new OperationHistoryRow(
                entry.Id,
                entry.OccurredAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture),
                serverName,
                database?.Engine is { } engine ? EngineDisplay(engine) : _localization.Get("Loc.OperationHistory.NotDatabaseOperation"),
                database?.DatabaseName ?? "—",
                entry.Category,
                RiskDisplay(entry.Risk),
                OutcomeDisplay(entry.Outcome),
                entry.Summary,
                entry.Target,
                VerificationDisplay(item.Verification),
                item.HasUnknownRemoteState,
                entry.Outcome,
                database));
        }

        if (selectedId is { } id)
        {
            HistoryGrid.SelectedItem = _rows.FirstOrDefault(row => row.Id == id);
        }

        if (HistoryGrid.SelectedItem is null && _rows.Count > 0)
        {
            HistoryGrid.SelectedIndex = 0;
        }

        UpdateDetails();
    }

    private void HistorySelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDetails();

    private void UpdateDetails()
    {
        if (HistoryGrid.SelectedItem is not OperationHistoryRow row)
        {
            KnownFactsText.Text = _localization.Get("Loc.OperationHistory.NoSelection");
            VerificationText.Text = _localization.Get("Loc.OperationHistory.VerificationUnavailable");
            UnknownFactsText.Text = _localization.Get("Loc.OperationHistory.NoSelection");
            TargetTextBox.Text = string.Empty;
            return;
        }

        var known = _localization.Format(
            "Loc.OperationHistory.KnownFormat",
            row.OccurredAtLocal,
            row.ServerName,
            row.Category,
            row.RiskDisplay,
            row.OutcomeDisplay,
            row.Summary);
        if (row.DatabaseContext is { } database)
        {
            known += Environment.NewLine + _localization.Format(
                "Loc.OperationHistory.DatabaseKnownFormat",
                database.DatabaseProfileId,
                database.Engine is { } engine ? EngineDisplay(engine) : _localization.Get("Loc.OperationHistory.UnknownDatabaseEngine"),
                database.DatabaseName,
                database.BackupId?.ToString("D") ?? _localization.Get("Loc.OperationHistory.NoBackupId"),
                database.Operation);
        }

        KnownFactsText.Text = known;
        VerificationText.Text = row.VerificationDisplay;
        UnknownFactsText.Text = row.Outcome switch
        {
            OperationOutcome.Unknown => _localization.Get("Loc.OperationHistory.UnknownAmbiguous"),
            OperationOutcome.Cancelled => _localization.Get("Loc.OperationHistory.UnknownCancelled"),
            _ => _localization.Get("Loc.OperationHistory.UnknownNone"),
        };
        TargetTextBox.Text = row.Target ?? _localization.Get("Loc.OperationHistory.TargetUnavailable");
    }

    private string RiskDisplay(OperationRisk risk) => risk switch
    {
        OperationRisk.ReadOnly => _localization.Get("Loc.OperationHistory.RiskReadOnly"),
        OperationRisk.Mutating => _localization.Get("Loc.OperationHistory.RiskMutating"),
        OperationRisk.Destructive => _localization.Get("Loc.OperationHistory.RiskDestructive"),
        _ => risk.ToString(),
    };

    private string OutcomeDisplay(OperationOutcome outcome) => outcome switch
    {
        OperationOutcome.Succeeded => _localization.Get("Loc.OperationHistory.OutcomeSucceeded"),
        OperationOutcome.Failed => _localization.Get("Loc.OperationHistory.OutcomeFailed"),
        OperationOutcome.Cancelled => _localization.Get("Loc.OperationHistory.OutcomeCancelled"),
        OperationOutcome.Unknown => _localization.Get("Loc.OperationHistory.OutcomeUnknown"),
        _ => outcome.ToString(),
    };

    private static string EngineDisplay(DatabaseEngineKind engine) => engine switch
    {
        DatabaseEngineKind.PostgreSql => "PostgreSQL",
        DatabaseEngineKind.MySql => "MySQL",
        DatabaseEngineKind.MariaDb => "MariaDB",
        DatabaseEngineKind.Redis => "Redis",
        DatabaseEngineKind.SqlServer => "Microsoft SQL Server",
        _ => engine.ToString(),
    };

    private string BuildCertificationSummary()
    {
        var rows = new List<string>();
        foreach (var entry in DatabaseCertificationMatrix.Entries)
        {
            rows.Add(_localization.Format(
                "Loc.OperationHistory.CertificationRowFormat",
                EngineDisplay(entry.Engine),
                entry.Version,
                CertificationLevelDisplay(DatabaseCertificationMatrix.LevelFor(entry.Engine, entry.Version, DatabaseCapabilityKind.RuntimeInventory)),
                CertificationLevelDisplay(DatabaseCertificationMatrix.LevelFor(entry.Engine, entry.Version, DatabaseCapabilityKind.SshTunneledConnectivity)),
                CertificationLevelDisplay(DatabaseCertificationMatrix.LevelFor(entry.Engine, entry.Version, DatabaseCapabilityKind.Diagnostics)),
                CertificationLevelDisplay(DatabaseCertificationMatrix.LevelFor(entry.Engine, entry.Version, DatabaseCapabilityKind.Backup)),
                CertificationLevelDisplay(DatabaseCertificationMatrix.LevelFor(entry.Engine, entry.Version, DatabaseCapabilityKind.Restore))));
        }

        return string.Join(Environment.NewLine, rows);
    }

    private string CertificationLevelDisplay(DatabaseCertificationLevel level) => level switch
    {
        DatabaseCertificationLevel.Certified => _localization.Get("Loc.OperationHistory.CertificationCertified"),
        DatabaseCertificationLevel.Tested => _localization.Get("Loc.OperationHistory.CertificationTested"),
        DatabaseCertificationLevel.Unsupported => _localization.Get("Loc.OperationHistory.CertificationUnsupported"),
        _ => level.ToString(),
    };

    private string VerificationDisplay(string? verification) => verification switch
    {
        "backup-verified" => _localization.Get("Loc.OperationHistory.VerificationBackupVerified"),
        "restore-target-verified" => _localization.Get("Loc.OperationHistory.VerificationRestoreVerified"),
        "unsupported" => _localization.Get("Loc.OperationHistory.VerificationUnsupported"),
        "post-state-verified" => _localization.Get("Loc.OperationHistory.VerificationSucceeded"),
        "failed-known" => _localization.Get("Loc.OperationHistory.VerificationFailed"),
        "cancelled" => _localization.Get("Loc.OperationHistory.VerificationCancelled"),
        "ambiguous-unknown" => _localization.Get("Loc.OperationHistory.VerificationUnknown"),
        null => _localization.Get("Loc.OperationHistory.VerificationLegacy"),
        _ => verification,
    };

    private void SetBusy(bool value)
    {
        _busy = value;
        LoadButton.IsEnabled = !value;
        HistoryGrid.IsEnabled = !value;
    }

    private static DateTimeOffset? ToStartOfDayUtc(DateTime? value)
    {
        if (value is null)
        {
            return null;
        }

        var local = DateTime.SpecifyKind(value.Value.Date, DateTimeKind.Local);
        return new DateTimeOffset(local).ToUniversalTime();
    }

    private static DateTimeOffset? ToEndOfDayUtc(DateTime? value)
    {
        if (value is null)
        {
            return null;
        }

        var local = DateTime.SpecifyKind(value.Value.Date.AddDays(1).AddTicks(-1), DateTimeKind.Local);
        return new DateTimeOffset(local).ToUniversalTime();
    }

    private sealed record ServerFilterOption(Guid? Id, string Text);
    private sealed record RiskFilterOption(OperationRisk? Value, string Text);
    private sealed record OutcomeFilterOption(OperationOutcome? Value, string Text);
    private sealed record DatabaseEngineFilterOption(DatabaseEngineKind? Value, string Text);
}

public sealed record OperationHistoryRow(
    Guid Id,
    string OccurredAtLocal,
    string ServerName,
    string DatabaseEngine,
    string DatabaseName,
    string Category,
    string RiskDisplay,
    string OutcomeDisplay,
    string Summary,
    string? Target,
    string VerificationDisplay,
    bool HasUnknownRemoteState,
    OperationOutcome Outcome,
    DatabaseOperationAuditContext? DatabaseContext);
