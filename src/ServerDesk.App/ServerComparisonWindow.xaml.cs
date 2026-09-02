using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ServerDesk.App.Localization;
using ServerDesk.Application.Dashboard;

namespace ServerDesk.App;

public partial class ServerComparisonWindow : Window
{
    private readonly MultiServerComparisonResult _comparison;
    private readonly ILocalizationService _localization;

    public ServerComparisonWindow(
        MultiServerComparisonResult comparison,
        ILocalizationService localization)
    {
        InitializeComponent();
        _comparison = comparison ?? throw new ArgumentNullException(nameof(comparison));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        Rows = [];
        ComparisonGrid.ItemsSource = Rows;
        Closed += ServerComparisonWindowOnClosed;
        _localization.LanguageChanged += LocalizationOnLanguageChanged;
        RenderComparison();
    }

    public ObservableCollection<ServerComparisonRowViewModel> Rows { get; }

    private void ServerComparisonWindowOnClosed(object? sender, EventArgs e)
    {
        Closed -= ServerComparisonWindowOnClosed;
        _localization.LanguageChanged -= LocalizationOnLanguageChanged;
    }

    private void LocalizationOnLanguageChanged()
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(RenderComparison);
    }

    private void RenderComparison()
    {
        TargetsTextBlock.Text = _localization.Format(
            "Loc.GlobalDashboard.ComparisonTargets",
            _comparison.Servers.Count);
        ComparisonGrid.Columns.Clear();
        ComparisonGrid.Columns.Add(new DataGridTextColumn
        {
            Header = _localization.Get("Loc.GlobalDashboard.ComparisonColumnFact"),
            Binding = new Binding(nameof(ServerComparisonRowViewModel.FactDisplay)),
            Width = new DataGridLength(210),
        });
        ComparisonGrid.Columns.Add(new DataGridTextColumn
        {
            Header = _localization.Get("Loc.GlobalDashboard.ComparisonColumnStatus"),
            Binding = new Binding(nameof(ServerComparisonRowViewModel.StatusDisplay)),
            Width = new DataGridLength(120),
        });

        for (var index = 0; index < _comparison.Servers.Count; index++)
        {
            var server = _comparison.Servers[index];
            ComparisonGrid.Columns.Add(new DataGridTextColumn
            {
                Header = $"{server.Name}\n{server.Endpoint}",
                Binding = new Binding($"Values[{index}]"),
                Width = new DataGridLength(180),
            });
        }

        Rows.Clear();
        foreach (var fact in _comparison.Facts)
        {
            Rows.Add(new ServerComparisonRowViewModel(fact, _localization));
        }
    }

    private void CloseOnClick(object sender, RoutedEventArgs e) => Close();
}

public sealed class ServerComparisonRowViewModel
{
    public ServerComparisonRowViewModel(
        MultiServerComparisonFactResult result,
        ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(localization);
        FactDisplay = localization.Get(GetFactKey(result.Fact));
        StatusDisplay = localization.Get(result.Status switch
        {
            MultiServerComparisonFactStatus.Equal => "Loc.GlobalDashboard.ComparisonStatusEqual",
            MultiServerComparisonFactStatus.Different => "Loc.GlobalDashboard.ComparisonStatusDifferent",
            _ => "Loc.GlobalDashboard.ComparisonStatusIncomplete",
        });
        Values = result.Values
            .Select(value => RenderValue(result.Fact, value, localization))
            .ToArray();
    }

    public string FactDisplay { get; }

    public string StatusDisplay { get; }

    public IReadOnlyList<string> Values { get; }

    private static string RenderValue(
        MultiServerComparisonFact fact,
        MultiServerComparisonValue value,
        ILocalizationService localization)
    {
        return value.Status switch
        {
            MultiServerComparisonValueStatus.Unknown =>
                localization.Get("Loc.GlobalDashboard.ComparisonValueUnknown"),
            MultiServerComparisonValueStatus.Unsupported =>
                localization.Get("Loc.GlobalDashboard.ComparisonValueUnsupported"),
            MultiServerComparisonValueStatus.Available
                when fact == MultiServerComparisonFact.CriticalWarningPresent =>
                localization.Get(value.CanonicalValue == "true"
                    ? "Loc.GlobalDashboard.ComparisonValueYes"
                    : "Loc.GlobalDashboard.ComparisonValueNo"),
            _ => value.DisplayValue ?? localization.Get("Loc.GlobalDashboard.ComparisonValueUnknown"),
        };
    }

    private static string GetFactKey(MultiServerComparisonFact fact) => fact switch
    {
        MultiServerComparisonFact.Environment => "Loc.GlobalDashboard.ComparisonFactEnvironment",
        MultiServerComparisonFact.SshPort => "Loc.GlobalDashboard.ComparisonFactSshPort",
        MultiServerComparisonFact.LogicalProcessors => "Loc.GlobalDashboard.ComparisonFactLogicalProcessors",
        MultiServerComparisonFact.TotalMemory => "Loc.GlobalDashboard.ComparisonFactTotalMemory",
        MultiServerComparisonFact.SwapTotal => "Loc.GlobalDashboard.ComparisonFactSwapTotal",
        MultiServerComparisonFact.CpuUtilization => "Loc.GlobalDashboard.ComparisonFactCpuUtilization",
        MultiServerComparisonFact.MemoryUtilization => "Loc.GlobalDashboard.ComparisonFactMemoryUtilization",
        MultiServerComparisonFact.HighestDiskUtilization => "Loc.GlobalDashboard.ComparisonFactDiskUtilization",
        MultiServerComparisonFact.WarningCount => "Loc.GlobalDashboard.ComparisonFactWarningCount",
        MultiServerComparisonFact.CriticalWarningPresent => "Loc.GlobalDashboard.ComparisonFactCriticalWarning",
        _ => "Loc.GlobalDashboard.ComparisonValueUnknown",
    };
}
