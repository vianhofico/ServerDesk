using ServerDesk.App.Localization;
using ServerDesk.App.Presentation;
using ServerDesk.Application.Profiles;
using ServerDesk.Application.Sessions;
using ServerDesk.Application.Settings;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class ShellSettingsPersistenceTests
{
    [Fact]
    public async Task ShutdownWaitsForQueuedSettingsAndLatestSnapshotWinsInOrder()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new BlockingSettingsStore();
        var viewModel = CreateViewModel(store);

        viewModel.ThemePreference = AppThemePreference.Dark;
        await store.FirstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);

        viewModel.LanguagePreference = AppLanguagePreference.Vietnamese;
        viewModel.ThemePreference = AppThemePreference.Light;

        var shutdownTask = viewModel.ShutdownAsync().AsTask();
        Assert.False(shutdownTask.IsCompleted);

        store.ReleaseFirstSave.TrySetResult();
        await shutdownTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        Assert.Equal(
            [
                new AppSettings(AppThemePreference.Dark, AppLanguagePreference.System),
                new AppSettings(AppThemePreference.Dark, AppLanguagePreference.Vietnamese),
                new AppSettings(AppThemePreference.Light, AppLanguagePreference.Vietnamese),
            ],
            store.Saved);
    }

    private static ShellViewModel CreateViewModel(IAppSettingsStore settingsStore) =>
        new(
            new NavigationService(),
            new RecordingThemeService(),
            new RecordingLocalizationService(),
            settingsStore,
            new UnusedServerProfileService(),
            new UnusedRemoteSessionFactory());

    private sealed class BlockingSettingsStore : IAppSettingsStore
    {
        private readonly object _sync = new();
        private readonly List<AppSettings> _saved = [];
        private int _saveCalls;

        public TaskCompletionSource FirstSaveStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstSave { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<AppSettings> Saved
        {
            get
            {
                lock (_sync)
                {
                    return _saved.ToArray();
                }
            }
        }

        public ValueTask<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(AppSettings.Default);

        public async ValueTask SaveAsync(
            AppSettings settings,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _saveCalls) == 1)
            {
                FirstSaveStarted.TrySetResult();
                await ReleaseFirstSave.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            lock (_sync)
            {
                _saved.Add(settings);
            }
        }
    }

    private sealed class RecordingThemeService : IThemeService
    {
        public AppThemeKind EffectiveTheme { get; private set; } = AppThemeKind.Light;

        public void Apply(AppThemePreference preference)
        {
            EffectiveTheme = preference == AppThemePreference.Dark
                ? AppThemeKind.Dark
                : AppThemeKind.Light;
        }
    }

    private sealed class RecordingLocalizationService : ILocalizationService
    {
        public AppLanguageKind EffectiveLanguage { get; private set; } = AppLanguageKind.English;

        public string EffectiveCultureCode => LanguagePreferenceResolver.GetCultureCode(EffectiveLanguage);

        public event Action? LanguageChanged;

        public void Apply(AppLanguagePreference preference)
        {
            EffectiveLanguage = preference == AppLanguagePreference.Vietnamese
                ? AppLanguageKind.Vietnamese
                : AppLanguageKind.English;
            LanguageChanged?.Invoke();
        }

        public string Get(string key) => key;

        public string Format(string key, params object?[] arguments) => key;
    }

    private sealed class UnusedServerProfileService : IServerProfileService
    {
        public ValueTask<IReadOnlyList<ServerProfile>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<ServerProfile>>([]);

        public ValueTask<ServerProfile> CreateAsync(
            ServerProfileSpec spec,
            string? initialSecret,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Settings persistence tests must not create server profiles.");

        public ValueTask<ServerProfile> UpdateAsync(
            Guid id,
            ServerProfileSpec spec,
            string? replacementSecret,
            bool replaceSecret,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Settings persistence tests must not update server profiles.");

        public ValueTask DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Settings persistence tests must not delete server profiles.");
    }

    private sealed class UnusedRemoteSessionFactory : IRemoteSessionFactory
    {
        public IRemoteSession Create(ServerProfile profile) =>
            throw new InvalidOperationException("Settings persistence tests must not create remote sessions.");
    }
}
