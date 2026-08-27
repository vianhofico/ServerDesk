using System.ComponentModel;
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

    private async void CloseTabOnClick(object sender, RoutedEventArgs e)
    {
        await CloseSelectedTabAsync().ConfigureAwait(true);
    }

    private async void TerminalTabsOnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
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
        var tab = new TabItem
        {
            Header = $"{profile.Name} #{tabNumber} • connecting",
            Content = host,
        };

        host.StateChanged += state =>
        {
            if (!Dispatcher.HasShutdownStarted)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    tab.Header = $"{profile.Name} #{tabNumber} • {state.ToString().ToLowerInvariant()}";
                    if (ReferenceEquals(TerminalTabs.SelectedItem, tab))
                    {
                        StatusText.Text = $"{profile.Username}@{profile.Host}:{profile.Port} — {state}";
                    }
                });
            }
        };
        host.ErrorRaised += message =>
        {
            if (!Dispatcher.HasShutdownStarted)
            {
                Dispatcher.BeginInvoke(() => StatusText.Text = message);
            }
        };

        _hosts.Add(tab, host);
        TerminalTabs.Items.Add(tab);
        TerminalTabs.SelectedItem = tab;
        StatusText.Text = $"Opening terminal for {profile.Name}…";

        try
        {
            await host.InitializeAsync().ConfigureAwait(true);
            StatusText.Text = $"Connected to {profile.Username}@{profile.Host}:{profile.Port}";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = $"Terminal connection to {profile.Name} was cancelled.";
        }
        catch (TerminalSessionException exception)
        {
            StatusText.Text = exception.Error.Message;
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not open terminal: {exception.Message}";
        }
    }

    private async Task CloseSelectedTabAsync()
    {
        if (TerminalTabs.SelectedItem is not TabItem tab || !_hosts.Remove(tab, out var host))
        {
            return;
        }

        TerminalTabs.Items.Remove(tab);
        StatusText.Text = "Closing remote PTY…";
        await host.DisposeAsync().ConfigureAwait(true);
        StatusText.Text = _hosts.Count == 0 ? "No terminal tabs are open." : "Terminal tab closed.";
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
        StatusText.Text = "Closing remote terminal sessions…";
        var hosts = _hosts.Values.ToArray();
        _hosts.Clear();
        TerminalTabs.Items.Clear();

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
    private TerminalSize _requestedSize = TerminalSize.Default;
    private bool _disposed;

    public TerminalTabHost(IRemoteTerminalSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _webView = new WebView2();
        Children.Add(_webView);
    }

    public event Action<TerminalSessionState>? StateChanged;

    public event Action<string>? ErrorRaised;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        var token = linkedCancellation.Token;
        var frontendDirectory = Path.Combine(AppContext.BaseDirectory, "TerminalFrontend", "dist");
        var frontendEntry = Path.Combine(frontendDirectory, "index.html");
        if (!File.Exists(frontendEntry))
        {
            throw new FileNotFoundException(
                "The local terminal frontend bundle is missing. Rebuild ServerDesk so the xterm assets are generated.",
                frontendEntry);
        }

        _session.OutputReceived += SessionOnOutputReceived;
        _session.StateChanged += SessionOnStateChanged;

        await _webView.EnsureCoreWebView2Async().ConfigureAwait(true);
        var core = _webView.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = true;
        core.SetVirtualHostNameToFolderMapping(
            VirtualHost,
            frontendDirectory,
            CoreWebView2HostResourceAccessKind.DenyCors);
        core.WebMessageReceived += CoreOnWebMessageReceived;
        core.NavigationStarting += CoreOnNavigationStarting;
        core.Navigate($"{VirtualOrigin}index.html");

        await _frontendReady.Task.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(true);
        await _session.ConnectAsync(_requestedSize, token).ConfigureAwait(true);
        PostState(_session.State);
    }

    public Task FocusAsync()
    {
        if (_disposed || _webView.CoreWebView2 is null)
        {
            return Task.CompletedTask;
        }

        PostMessage(new { type = "focus" });
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetimeCancellation.Cancel();
        _session.OutputReceived -= SessionOnOutputReceived;
        _session.StateChanged -= SessionOnStateChanged;
        if (_webView.CoreWebView2 is { } core)
        {
            core.WebMessageReceived -= CoreOnWebMessageReceived;
            core.NavigationStarting -= CoreOnNavigationStarting;
        }

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
        if (!e.Uri.StartsWith(VirtualOrigin, StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            ErrorRaised?.Invoke("Terminal navigation outside the packaged local frontend was blocked.");
        }
    }

    private async void CoreOnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

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
                            ErrorRaised?.Invoke("Clipboard text is too large to paste into a terminal in one operation.");
                        }
                    }
                    break;
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (TerminalSessionException exception)
        {
            ErrorRaised?.Invoke(exception.Error.Message);
        }
        catch (Exception exception)
        {
            ErrorRaised?.Invoke($"Terminal bridge error: {exception.Message}");
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
        if (_disposed || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (!_disposed)
            {
                PostMessage(new { type = "output", data = chunk });
            }
        });
    }

    private void SessionOnStateChanged(TerminalSessionState state)
    {
        if (_disposed || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
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
        if (_disposed || _webView.CoreWebView2 is null)
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

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
