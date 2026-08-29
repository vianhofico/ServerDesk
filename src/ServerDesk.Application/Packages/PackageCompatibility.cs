using ServerDesk.Application.Remote;

namespace ServerDesk.Application.Packages;

internal static class PackageStringCompatibilityExtensions
{
    internal static bool StartsWith(this string value, char character, StringComparison comparisonType) =>
        value.StartsWith(character.ToString(), comparisonType);
}

public sealed partial class PackageAdministrationService
{
    private Task<ReadResult> ReadAsync(
        IRemoteCommandExecutor executor,
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        int[] acceptedExitCodes) =>
        ReadAsync(
            executor,
            executable,
            arguments,
            cancellationToken,
            new HashSet<int>(acceptedExitCodes));
}
