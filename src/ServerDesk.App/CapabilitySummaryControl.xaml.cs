using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ServerDesk.App.Presentation;
using ServerDesk.Application.Capabilities;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class CapabilitySummaryControl : UserControl, IDisposable
{
    private readonly IServerCapabilityService _capabilityService;
    private readonly CapabilitySummaryViewModel _viewModel = new();
    private ServerProfile? _profile;
    private CancellationTokenSource? _scanCancellation;
    private bool _disposed;

    public CapabilitySummaryControl(IServerCapabilityService capabilityService)
    {
        InitializeComponent();
        _capabilityService = capabilityService ?? throw new ArgumentNullException(nameof(capabilityService));
        DataContext = _viewModel;
    }

    public void SetServer(ServerProfile? profile, bool isConnected)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var changed = _profile?.Id != profile?.Id;
        if (changed)
        {
            CancelScan();
            _profile = profile;
            _viewModel.Clear(profile);
        }

        _viewModel.IsConnected = isConnected;
        if (profile is not null && isConnected && !_viewModel.HasSnapshot && !_viewModel.IsBusy)
        {
            _ = ScanAsync(forceRefresh: false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelScan();
    }

    private async void RefreshOnClick(object sender, RoutedEventArgs e)
    {
        await ScanAsync(forceRefresh: true).ConfigureAwait(true);
    }

    private async Task ScanAsync(bool forceRefresh)
    {
        var profile = _profile;
        if (_disposed || profile is null || !_viewModel.IsConnected)
        {
            return;
        }

        CancelScan();
        var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        _viewModel.IsBusy = true;
        _viewModel.ErrorMessage = null;
        try
        {
            var snapshot = await _capabilityService
                .GetAsync(profile, forceRefresh, cancellation.Token)
                .ConfigureAwait(true);
            if (!_disposed && _profile?.Id == profile.Id && !cancellation.IsCancellationRequested)
            {
                _viewModel.Apply(snapshot);
            }
        }
        catch (OperationCanceledException)
        {
            // A server selection change or window close intentionally cancels the in-flight scan.
        }
        catch (Exception exception)
        {
            if (!_disposed && _profile?.Id == profile.Id)
            {
                _viewModel.ErrorMessage = $"Capability scan failed: {exception.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(_scanCancellation, cancellation))
            {
                _scanCancellation = null;
                _viewModel.IsBusy = false;
            }

            cancellation.Dispose();
        }
    }

    private void CancelScan()
    {
        if (_scanCancellation is null)
        {
            return;
        }

        _scanCancellation.Cancel();
        _scanCancellation = null;
    }
}

internal sealed class CapabilitySummaryViewModel : ObservableObject
{
    private ServerProfile? _profile;
    private bool _isConnected;
    private bool _isBusy;
    private ServerCapabilitySnapshot? _snapshot;
    private string? _errorMessage;
    private IReadOnlyList<CapabilityRowViewModel> _capabilityRows = [];

    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (!SetProperty(ref _isConnected, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanRefresh));
            OnPropertyChanged(nameof(SummaryText));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (!SetProperty(ref _isBusy, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanRefresh));
            OnPropertyChanged(nameof(SummaryText));
        }
    }

    public bool HasSnapshot => _snapshot is not null;

    public bool CanRefresh => _profile is not null && IsConnected && !IsBusy;

    public string SummaryText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ErrorMessage))
            {
                return ErrorMessage;
            }

            if (_profile is null)
            {
                return "Select a server to inspect its Linux capabilities.";
            }

            if (IsBusy)
            {
                return $"Scanning {_profile.Name} through a read-only SSH command channel…";
            }

            if (_snapshot is not null)
            {
                return IsConnected
                    ? $"Normalized read-only snapshot for {_snapshot.Identity.DisplayName}."
                    : $"Cached snapshot for {_snapshot.Identity.DisplayName}. Reconnect to refresh it.";
            }

            return IsConnected
                ? "Connected. Capability discovery is ready."
                : "Connect this server to detect OS, tools, databases and privilege capabilities.";
        }
    }

    public string OperatingSystem => _snapshot?.Identity.DisplayName ?? "—";

    public string ArchitectureKernel => _snapshot is null
        ? "—"
        : $"{_snapshot.Identity.Architecture} · {_snapshot.Identity.KernelVersion}";

    public string RemoteIdentity => _snapshot is null
        ? "—"
        : _snapshot.Identity.UserId is { } uid
            ? $"{_snapshot.Identity.CurrentUser} · uid {uid}{(_snapshot.Identity.IsRoot ? " · root" : string.Empty)}"
            : _snapshot.Identity.CurrentUser;

    public string CapturedText => _snapshot is null
        ? string.Empty
        : $"Captured {_snapshot.CapturedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)} · cache refreshes after 5 minutes or on demand.";

    public string? ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(SummaryText));
            }
        }
    }

    public IReadOnlyList<CapabilityRowViewModel> CapabilityRows
    {
        get => _capabilityRows;
        private set => SetProperty(ref _capabilityRows, value);
    }

    public void Clear(ServerProfile? profile)
    {
        _profile = profile;
        _snapshot = null;
        ErrorMessage = null;
        CapabilityRows = [];
        OnPropertyChanged(nameof(HasSnapshot));
        OnPropertyChanged(nameof(OperatingSystem));
        OnPropertyChanged(nameof(ArchitectureKernel));
        OnPropertyChanged(nameof(RemoteIdentity));
        OnPropertyChanged(nameof(CapturedText));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(SummaryText));
    }

    public void Apply(ServerCapabilitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
        ErrorMessage = null;
        CapabilityRows =
        [
            From("systemd", snapshot.Systemd),
            From("Docker", snapshot.Docker),
            From("Docker Compose", snapshot.DockerCompose),
            From("nginx", snapshot.Nginx),
            From("Apache", snapshot.Apache),
            From("Git", snapshot.Git),
            From("UFW", snapshot.Ufw),
            From("firewalld", snapshot.Firewalld),
            From("PostgreSQL", snapshot.PostgreSql),
            From("MySQL / MariaDB", snapshot.MySql),
            From("Redis", snapshot.Redis),
            FromSudo(snapshot.Sudo),
        ];

        OnPropertyChanged(nameof(HasSnapshot));
        OnPropertyChanged(nameof(OperatingSystem));
        OnPropertyChanged(nameof(ArchitectureKernel));
        OnPropertyChanged(nameof(RemoteIdentity));
        OnPropertyChanged(nameof(CapturedText));
        OnPropertyChanged(nameof(SummaryText));
    }

    private static CapabilityRowViewModel From(string name, CapabilityState state) =>
        new(name, StatusLabel(state.Status), BuildDetail(state.Version, state.Detail));

    private static CapabilityRowViewModel FromSudo(SudoCapabilityState state)
    {
        var passwordless = state.Passwordless switch
        {
            true => "passwordless: yes",
            false => "passwordless: no",
            null => null,
        };
        return new CapabilityRowViewModel(
            "sudo",
            StatusLabel(state.Status),
            BuildDetail(state.Version, passwordless, state.Detail));
    }

    private static string StatusLabel(CapabilityStatus status) => status switch
    {
        CapabilityStatus.Available => "Available",
        CapabilityStatus.Unavailable => "Unavailable",
        CapabilityStatus.PermissionDenied => "Permission denied",
        CapabilityStatus.Unknown => "Unknown",
        _ => "Unknown",
    };

    private static string BuildDetail(params string?[] values) =>
        string.Join(" · ", values.Where(value => !string.IsNullOrWhiteSpace(value)));
}

internal sealed record CapabilityRowViewModel(string Name, string StatusText, string DetailText);
