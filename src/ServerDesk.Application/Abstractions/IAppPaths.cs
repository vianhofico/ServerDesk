namespace ServerDesk.Application.Abstractions;

public interface IAppPaths
{
    string RootDirectory { get; }

    string DataDirectory { get; }

    string LogsDirectory { get; }

    string SettingsFilePath { get; }

    string DatabaseFilePath { get; }
}
