using System.Reflection;
using System.Runtime.InteropServices;

namespace ServerDesk.Agent;

public sealed record AgentRuntimeInfo(
    string Version,
    string Platform,
    string Architecture,
    DateTimeOffset StartedAtUtc)
{
    public static AgentRuntimeInfo Create()
    {
        var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString();
        var version = string.IsNullOrWhiteSpace(assemblyVersion) ? "0.0.0" : assemblyVersion;
        var platform = OperatingSystem.IsLinux() ? "linux" : "unsupported";
        var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        return new AgentRuntimeInfo(version, platform, architecture, DateTimeOffset.UtcNow);
    }
}
