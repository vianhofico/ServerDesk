using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ServerDesk.Application.Terminal;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class TerminalWindow : Window
{
    private readonly IRemoteTerminalSessionFactory _terminalFactory;
    private readonly IReadOnlyList<ServerProfile> _profiles;
    private readonly ServerProfile _initialProfile;
    private readonly Dictionary<TabItem, TerminalTabHost> _hosts = [];
    private bool _loaded;
    private bool _closing;
    private bool _allowClose;
    private int _tabSequence;

    public TerminalWindow(
        IRemoteTerminalSessionFactory terminalFactory,
        IReadOnlyList<ServerProfile> profiles,
        ServerProfile initialProfile)
    {
        InitializeComponent();
        _terminalFactory = terminalFactory ?? throw new ArgumentNullException(nameof(terminalFactory));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _initialProfile = initialProfile ?? throw new ArgumentNullException(nameof(initialProfile));

        ServerPicker.ItemsSource = _profiles;
        ServerPicker.SelectedItem = _profiles.FirstOrDefault(profile => profile.Id == initialProfile.Id) ?? initialProfile;
        Loaded += TerminalWindowOnLoaded;
        Closing += TerminalWindowOnClosing;
        UpdateTabActions();
    }

    private async void TerminalWindowOnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        await OpenTabAsync(_initialProfile).ConfigureAwait(true);
    }

    private async void NewTerminalOnClick(object sender, RoutedEventArgs e)
    {
        if (ServerPicker.SelectedItem is ServerProfile profile)
        {
            await OpenTabAsync(profile).ConfigureAwait(true);
        }
    }

    private async void ReconnectOnClick(object sender, RoutedEventArgs e)
    {
        await ReconnectSelectedTabAsync().ConfigureAwait(true);
    }

    private async void CloseTabOnClick(object sender, RoutedEventArgs e)
    {
        await CloseSelectedTabAsync().ConfigureAwait(true);
    }

    private async void TerminalTabsOnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateTabActions();
        if (TerminalTabs.SelectedItem is TabItem tab && _hosts.TryGetValue(tab, out var host))
        {
            await host.FocusAsync().ConfigureAwait(true);
        }
    }

    private async Task OpenTabAsync(ServerProfile profile)
    {
        var session = _terminalFactory.Create(profile);
        var host = new TerminalTabHost(session);
        var tabNumber = ++_tabSequence;
        var metadata = new TerminalTabMetadata(profile, tabNumber);
        var tab = new TabItem
        {
            Header = TerminalPresentationText.Format("Loc.Terminal.Tab.ConnectingFormat", profile.Name, tabNumber),
            Content = host,
            Tag = metadata,
        };

        AttachHostHandlers(tab, host, metadata);
        _hosts.Add(tab, host);
        TerminalTabs.Items.Add(tab);
        TerminalTabs.SelectedItem = tab;
        UpdateTabActions();
        StatusText.Text = TerminalPresentationText.Format("Loc.Terminal.Status.OpeningFormat", profile.Name);

        await InitializeHostAsync(host, profile).ConfigureAwait(true);
        UpdateTabActions();
    }

    private async Task ReconnectSelectedTabAsync()
    {
        if (TerminalTabs.SelectedItem is not TabItem tab ||
            !_hosts.TryGetValue(tab, out var currentHost) ||
            tab.Tag is not TerminalTabMetadata metadata ||
            currentHost.State is not (TerminalSessionState.Disconnected or TerminalSessionState.Faulted))
        {
            return;
        }

        ReconnectButton.IsEnabled = false;
        CloseTabButton.IsEnabled = false;
        StatusText.Text = TerminalPresentationText.Format(
            "Loc.Terminal.Status.ReconnectingFormat",
            metadata.Profile.Name);

        try
        {
            await currentHost.DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            StatusText.Text = TerminalPresentationText.Format("Loc.Terminal.Status.OpenErrorFormat", exception.Message);
            UpdateTabActions();
            return;
        }

        var replacement = new TerminalTabHost(_terminalFactory.Create(metadata.Profile));
        _hosts[tab] = replacement;
        tab.Content = replacement;
        tab.Header = TerminalPresentationText.Format(
            "Loc.Terminal.Tab.ConnectingFormat",
            metadata.Profile.Name,
            metadata.TabNumber);
        AttachHostHandlers(tab, replacement, metadata);
        UpdateTabActions();

        await InitializeHostAsync(replacement, metadata.Profile).ConfigureAwait(true);
        UpdateTabActions();
    }

    private void AttachHostHandlers(TabItem tab, TerminalTabHost host, TerminalTabMetadata metadata)
    {
        host.StateChanged += state =>
        {
            if (!Dispatcher.HasShutdownStarted)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    tab.Header = TerminalPresentationText.Format(
                        "Loc.Terminal.Tab.StateFormat",
                        metadata.Profile.Name,
                        metadata.TabNumber,
                        TerminalPresentationText.State(state));
                    if (ReferenceEquals(TerminalTabs.SelectedItem, tab))
                    {
                        StatusText.Text = TerminalPresentationText.Format(
                            "Loc.Terminal.Status.EndpointStateFormat",
                            Endpoint(metadata.Profile),
                            TerminalPresentationText.State(state));
                    }

                    UpdateTabActions();
                });
            }
        };
        host.ErrorRaised += message =>
        {
            if (!Dispatcher.HasShutdownStarted)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    StatusText.Text = message;
                    UpdateTabActions();
                });
            }
        };
    }

    private async Task InitializeHostAsync(TerminalTabHost host, ServerProfile profile)
    {
        try
        {
            await host.InitializeAsync().ConfigureAwait(true);
            StatusText.Text = TerminalPresentationText.Format("Loc.Terminal.Status.ConnectedFormat", Endpoint(profile));
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = TerminalPresentationText.Format("Loc.Terminal.Status.ConnectCancelledFormat", profile.Name);
        }
        catch (TerminalSessionException exception)
        {
            StatusText.Text = exception.Error.Message;
        }
        catch (Exception exception)
        {
            StatusText.Text = TerminalPresentationText.Format("Loc.Terminal.Status.OpenErrorFormat", exception.Message);
        }
    }

    private async Task CloseSelectedTabAsync()
    {
        if (TerminalTabs.SelectedItem is not TabItem tab || !_hosts.Remove(tab, out var host))
        {
            return;
        }

        TerminalTabs.Items.Remove(tab);
        UpdateTabActions();
        StatusText.Text = TerminalPresentationText.Get("Loc.Terminal.Status.ClosingPty");
        await host.DisposeAsync().ConfigureAwait(true);
        StatusText.Text = TerminalPresentationText.Get(
            _hosts.Count == 0 ? "Loc.Terminal.Status.NoTabs" : "Loc.Terminal.Status.TabClosed");
        UpdateTabActions();
    }

    private void UpdateTabActions()
    {
        if (TerminalTabs.SelectedItem is TabItem tab && _hosts.TryGetValue(tab, out var host))
        {
            CloseTabButton.IsEnabled = true;
            ReconnectButton.IsEnabled = host.State is TerminalSessionState.Disconnected or TerminalSessionState.Faulted;
            return;
        }

        CloseTabButton.IsEnabled = false;
        ReconnectButton.IsEnabled = false;
    }

    private void TerminalWindowOnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (_closing)
        {
            return;
        }

        _closing = true;
        _ = CloseAllAndCloseAsync();
    }

    private async Task CloseAllAndCloseAsync()
    {
        StatusText.Text = TerminalPresentationText.Get("Loc.Terminal.Status.ClosingAll");
        var hosts = _hosts.Values.ToArray();
        _hosts.Clear();
        TerminalTabs.Items.Clear();
        UpdateTabActions();

        foreach (var host in hosts)
        {
            try
            {
                await host.DisposeAsync().ConfigureAwait(true);
            }
            catch
            {
                // Window shutdown must continue. Disposing the transport closes the local socket best-effort.
            }
        }

        _allowClose = true;
        Close();
    }

    private static string Endpoint(ServerProfile profile) =>
        $"{profile.Username}@{profile.Host}:{profile.Port}";

    private sealed record TerminalTabMetadata(ServerProfile Profile, int TabNumber);
}

internal static class TerminalPresentationText
{
    public static string Get(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as string ?? key;

    public static string Format(string key, params object?[] arguments)
    {
        var template = Get(key);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, arguments);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    public static string State(TerminalSessionState state) =>
        Get($"Loc.Terminal.State.{state}");
}

internal sealed class TerminalTabHost : Grid, IAsyncDisposable
{
    private const string VirtualHost = "terminal.serverdesk.local";
    private const string VirtualOrigin = "https://terminal.serverdesk.local/";
    private const int MaxBridgeTextLength = 1_000_000;

    private readonly IRemoteTerminalSession _session;
    private readonly WebView2 _webView;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _messageGate = new(1, 1);
    private readonly TaskCompletionSource _frontendReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AsyncOperationDrain _operations = new();
    private readonly object _disposeSync = new();
    private readonly TaskCompletionSource _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TerminalSize _requestedSize = TerminalSize.Default;
    private bool _disposeStarted;

    public TerminalTabHost(IRemoteTerminalSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _webView = new WebView2();
        Children.Add(_webView);
    }

    public event Action<TerminalSessionState>? StateChanged;

    public event Action<string>? ErrorRaised;

    public TerminalSessionState State => _session.State;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using var operation = _operations.EnterOrThrow(this);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        var token = linkedCancellation.Token;
        token.ThrowIfCancellationRequested();

        var frontendDirectory = Path.Combine(AppContext.BaseDirectory, "TerminalFrontend", "dist");
        var frontendEntry = Path.Combine(frontendDirectory, "index.html");
        if (!File.Exists(frontendEntry))
        {
            throw new FileNotFoundException(
                TerminalPresentationText.Get("Loc.Terminal.Bridge.MissingFrontend"),
                frontendEntry);
        }

        _session.OutputReceived += SessionOnOutputReceived;
        _session.StateChanged += SessionOnStateChanged;
        token.ThrowIfCancellationRequested();

        await _webView.EnsureCoreWebView2Async().ConfigureAwait(true);
        token.ThrowIfCancellationRequested();

        var core = _webView.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = true;
        core.SetVirtualHostNameToFolderMapping(
            VirtualHost,
            frontendDirectory,
            CoreWebView2HostResourceAccessKind.DenyCors);
        token.ThrowIfCancellationRequested();

        var initialized = _operations.TryRunWhileAccepting(() =>
        {
            core.WebMessageReceived += CoreOnWebMessageReceived;
            core.NavigationStarting += CoreOnNavigationStarting;
            core.Navigate($"{VirtualOrigin}index.html");
        });
        if (!initialized)
        {
            throw new OperationCanceledException(token);
        }

        await _frontendReady.Task.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(true);
        token.ThrowIfCancellationRequested();
        await _session.ConnectAsync(_requestedSize, token).ConfigureAwait(true);
        token.ThrowIfCancellationRequested();
        PostState(_session.State);
    }

    public Task FocusAsync()
    {
        if (_operations.IsStopping || _webView.CoreWebView2 is null)
        {
            return Task.CompletedTask;
        }

        PostMessage(new { type = "focus" });
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        var startDisposal = false;
        lock (_disposeSync)
        {
            if (!_disposeStarted)
            {
                _disposeStarted = true;
                startDisposal = true;
            }
        }

        if (startDisposal)
        {
            _ = CompleteDisposalAsync();
        }

        return new ValueTask(_disposeCompletion.Task);
    }

    private async Task CompleteDisposalAsync()
    {
        try
        {
            await DisposeCoreAsync().ConfigureAwait(true);
            _disposeCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            _disposeCompletion.TrySetException(exception);
        }
    }

    private async Task DisposeCoreAsync()
    {
        var drainTask = _operations.StopAndDrainAsync();
        _lifetimeCancellation.Cancel();
        _session.OutputReceived -= SessionOnOutputReceived;
        _session.StateChanged -= SessionOnStateChanged;
        if (_webView.CoreWebView2 is { } core)
        {
            core.WebMessageReceived -= CoreOnWebMessageReceived;
            core.NavigationStarting -= CoreOnNavigationStarting;
        }

        await drainTask.ConfigureAwait(true);
        try
        {
            await _session.DisposeAsync().ConfigureAwait(true);
        }
        finally
        {
            _webView.Dispose();
            _messageGate.Dispose();
            _lifetimeCancellation.Dispose();
        }
    }

    private void CoreOnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_operations.IsStopping)
        {
            e.Cancel = true;
            return;
        }

        if (!e.Uri.StartsWith(VirtualOrigin, StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            ErrorRaised?.Invoke(TerminalPresentationText.Get("Loc.Terminal.Bridge.NavigationBlocked"));
        }
    }

    private async void CoreOnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!_operations.TryEnter(out var operation))
        {
            return;
        }

        using (operation)
        {
            try
            {
                using var document = JsonDocument.Parse(e.WebMessageAsJson);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var typeElement))
                {
                    return;
                }

                var type = typeElement.GetString();
                switch (type)
                {
                    case "ready":
                        _requestedSize = ReadSize(root);
                        _frontendReady.TrySetResult();
                        break;

                    case "input":
                        if (_session.State == TerminalSessionState.Connected &&
                            TryReadBoundedText(root, "data", out var input))
                        {
                            await ExecuteSerializedAsync(
                                    cancellationToken => _session.SendAsync(input, cancellationToken),
                                    _lifetimeCancellation.Token)
                                .ConfigureAwait(true);
                        }
                        break;

                    case "resize":
                        _requestedSize = ReadSize(root);
                        if (_session.State == TerminalSessionState.Connected)
                        {
                            await ExecuteSerializedAsync(
                                    cancellationToken => _session.ResizeAsync(_requestedSize, cancellationToken),
                                    _lifetimeCancellation.Token)
                                .ConfigureAwait(true);
                        }
                        break;

                    case "copy":
                        if (TryReadBoundedText(root, "data", out var selection) && selection.Length > 0)
                        {
                            Clipboard.SetText(selection);
                        }
                        break;

                    case "pasteRequest":
                        if (Clipboard.ContainsText())
                        {
                            var clipboardText = Clipboard.GetText();
                            if (clipboardText.Length <= MaxBridgeTextLength)
                            {
                                PostMessage(new { type = "paste", data = clipboardText });
                            }
                            else
                            {
                                ErrorRaised?.Invoke(TerminalPresentationText.Get("Loc.Terminal.Bridge.ClipboardTooLarge"));
                            }
                        }
                        break;
                }
            }
            catch (OperationCanceledException) when (_operations.IsStopping)
            {
            }
            catch (ObjectDisposedException) when (_operations.IsStopping)
            {
            }
            catch (TerminalSessionException exception)
            {
                ErrorRaised?.Invoke(exception.Error.Message);
            }
            catch (Exception exception)
            {
                ErrorRaised?.Invoke(TerminalPresentationText.Format("Loc.Terminal.Bridge.ErrorFormat", exception.Message));
            }
        }
    }

    private async Task ExecuteSerializedAsync(
        Func<CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken)
    {
        await _messageGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            await operation(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _messageGate.Release();
        }
    }

    private void SessionOnOutputReceived(string chunk)
    {
        if (_operations.IsStopping || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (!_operations.IsStopping)
            {
                PostMessage(new { type = "output", data = chunk });
            }
        });
    }

    private void SessionOnStateChanged(TerminalSessionState state)
    {
        if (_operations.IsStopping || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_operations.IsStopping)
            {
                return;
            }

            StateChanged?.Invoke(state);
            PostState(state);
            if (state == TerminalSessionState.Faulted && _session.LastError is { } error)
            {
                ErrorRaised?.Invoke(error.Message);
            }
        });
    }

    private void PostState(TerminalSessionState state) =>
        PostMessage(new { type = "state", state = state.ToString() });

    private void PostMessage(object message)
    {
        if (_operations.IsStopping || _webView.CoreWebView2 is null)
        {
            return;
        }

        _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private static bool TryReadBoundedText(JsonElement root, string propertyName, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var candidate = element.GetString();
        if (candidate is null || candidate.Length > MaxBridgeTextLength)
        {
            return false;
        }

        value = candidate;
        return true;
    }

    private static TerminalSize ReadSize(JsonElement root)
    {
        var columns = root.TryGetProperty("columns", out var columnsElement) && columnsElement.TryGetUInt32(out var parsedColumns)
            ? parsedColumns
            : TerminalSize.Default.Columns;
        var rows = root.TryGetProperty("rows", out var rowsElement) && rowsElement.TryGetUInt32(out var parsedRows)
            ? parsedRows
            : TerminalSize.Default.Rows;
        return new TerminalSize(
            Math.Clamp(columns, 2u, 1000u),
            Math.Clamp(rows, 1u, 1000u));
    }
}
