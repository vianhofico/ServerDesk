from pathlib import Path


path = Path("src/ServerDesk.App/Presentation/Shell.cs")
text = path.read_text(encoding="utf-8")


def replace_once(old: str, new: str, label: str) -> None:
    global text
    if old not in text:
        raise SystemExit(f"Expected {label} was not found")
    text = text.replace(old, new, 1)


replace_once(
    """    private readonly Dictionary<Guid, CancellationTokenSource> _connectionCancellations = [];\n    private readonly SemaphoreSlim _settingsSaveGate = new(1, 1);\n    private NavigationItem _selectedNavigationItem;""",
    """    private readonly Dictionary<Guid, CancellationTokenSource> _connectionCancellations = [];\n    private readonly object _settingsSaveSync = new();\n    private Task _settingsSaveTail = Task.CompletedTask;\n    private bool _settingsSaveStopping;\n    private NavigationItem _selectedNavigationItem;""",
    "settings persistence fields",
)

if text.count("            _ = PersistSettingsAsync();") != 2:
    raise SystemExit("Expected exactly two fire-and-forget settings saves")
text = text.replace("            _ = PersistSettingsAsync();", "            QueueSettingsSave();")

replace_once(
    """    public async ValueTask ShutdownAsync()\n    {\n        _localizationService.LanguageChanged -= OnLanguageChanged;\n        foreach (var cancellation in _connectionCancellations.Values)\n        {\n            cancellation.Cancel();\n        }\n\n        foreach (var id in _sessions.Keys.ToArray())\n        {\n            await DisposeCachedSessionAsync(id).ConfigureAwait(false);\n        }\n    }""",
    """    public async ValueTask ShutdownAsync()\n    {\n        Task settingsSaveTail;\n        lock (_settingsSaveSync)\n        {\n            _settingsSaveStopping = true;\n            settingsSaveTail = _settingsSaveTail;\n        }\n\n        _localizationService.LanguageChanged -= OnLanguageChanged;\n        foreach (var cancellation in _connectionCancellations.Values)\n        {\n            cancellation.Cancel();\n        }\n\n        foreach (var id in _sessions.Keys.ToArray())\n        {\n            await DisposeCachedSessionAsync(id).ConfigureAwait(false);\n        }\n\n        try\n        {\n            await settingsSaveTail.ConfigureAwait(false);\n        }\n        catch\n        {\n            // App shutdown must continue. Expected settings I/O/access failures are handled by the save path.\n        }\n    }""",
    "ShutdownAsync",
)

replace_once(
    """    private async Task PersistSettingsAsync()\n    {\n        await _settingsSaveGate.WaitAsync().ConfigureAwait(true);\n        try\n        {\n            await _settingsStore.SaveAsync(\n                new AppSettings(_themePreference, _languagePreference)).ConfigureAwait(true);\n            SettingsMessage = null;\n        }\n        catch (System.IO.IOException)\n        {\n            SettingsMessage = _localizationService.Get(\"Loc.Settings.SaveFailed\");\n        }\n        catch (UnauthorizedAccessException)\n        {\n            SettingsMessage = _localizationService.Get(\"Loc.Settings.SaveFailed\");\n        }\n        finally\n        {\n            _settingsSaveGate.Release();\n        }\n    }""",
    """    private void QueueSettingsSave()\n    {\n        var settings = new AppSettings(_themePreference, _languagePreference);\n        lock (_settingsSaveSync)\n        {\n            if (_settingsSaveStopping)\n            {\n                return;\n            }\n\n            var previous = _settingsSaveTail;\n            _settingsSaveTail = Task.Run(\n                () => PersistSettingsAfterAsync(previous, settings),\n                CancellationToken.None);\n        }\n    }\n\n    private async Task PersistSettingsAfterAsync(Task previous, AppSettings settings)\n    {\n        try\n        {\n            await previous.ConfigureAwait(false);\n        }\n        catch\n        {\n            // Keep later accepted settings writes alive even if an unexpected earlier save failed.\n        }\n\n        await PersistSettingsSnapshotAsync(settings).ConfigureAwait(false);\n    }\n\n    private async Task PersistSettingsSnapshotAsync(AppSettings settings)\n    {\n        try\n        {\n            await _settingsStore.SaveAsync(settings).ConfigureAwait(false);\n            PostSettingsMessage(saveFailed: false);\n        }\n        catch (System.IO.IOException)\n        {\n            PostSettingsMessage(saveFailed: true);\n        }\n        catch (UnauthorizedAccessException)\n        {\n            PostSettingsMessage(saveFailed: true);\n        }\n    }\n\n    private void PostSettingsMessage(bool saveFailed)\n    {\n        void Apply() => SettingsMessage = saveFailed\n            ? _localizationService.Get(\"Loc.Settings.SaveFailed\")\n            : null;\n\n        if (_uiContext is null || ReferenceEquals(SynchronizationContext.Current, _uiContext))\n        {\n            Apply();\n            return;\n        }\n\n        _uiContext.Post(_ => Apply(), null);\n    }""",
    "PersistSettingsAsync",
)

path.write_text(text, encoding="utf-8")
