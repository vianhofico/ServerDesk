using System.Text.Json;
using Microsoft.Win32;
using ServerDesk.Application.Abstractions;
using ServerDesk.Application.Settings;

namespace ServerDesk.Platform.Windows;

public sealed class WindowsAppPaths : IAppPaths
{
    public WindowsAppPaths()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
    {
    }

    public WindowsAppPaths(string localApplicationDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationDataRoot);

        RootDirectory = Path.Combine(localApplicationDataRoot, "ServerDesk");
        DataDirectory = Path.Combine(RootDirectory, "data");
        LogsDirectory = Path.Combine(RootDirectory, "logs");
        SettingsFilePath = Path.Combine(RootDirectory, "settings.json");
        DatabaseFilePath = Path.Combine(DataDirectory, "serverdesk.db");

        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    public string RootDirectory { get; }

    public string DataDirectory { get; }

    public string LogsDirectory { get; }

    public string SettingsFilePath { get; }

    public string DatabaseFilePath { get; }
}

public sealed class JsonAppSettingsStore : IAppSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly IAppPaths _appPaths;

    public JsonAppSettingsStore(IAppPaths appPaths)
    {
        _appPaths = appPaths;
    }

    public async ValueTask<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_appPaths.SettingsFilePath))
        {
            return AppSettings.Default;
        }

        try
        {
            await using var stream = File.OpenRead(_appPaths.SettingsFilePath);
            return await JsonSerializer.DeserializeAsync<AppSettings>(
                    stream,
                    SerializerOptions,
                    cancellationToken)
                .ConfigureAwait(false) ?? AppSettings.Default;
        }
        catch (JsonException)
        {
            return AppSettings.Default;
        }
    }

    public async ValueTask SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var temporaryPath = _appPaths.SettingsFilePath + ".tmp";
        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         4096,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(
                    stream,
                    settings,
                    SerializerOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, _appPaths.SettingsFilePath, true);
    }
}

public sealed class WindowsSystemThemeDetector : ISystemThemeDetector
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    public AppThemeKind GetCurrentTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            var value = key?.GetValue(AppsUseLightThemeValue);
            return value is int intValue && intValue == 0
                ? AppThemeKind.Dark
                : AppThemeKind.Light;
        }
        catch (System.Security.SecurityException)
        {
            return AppThemeKind.Light;
        }
        catch (UnauthorizedAccessException)
        {
            return AppThemeKind.Light;
        }
    }
}
