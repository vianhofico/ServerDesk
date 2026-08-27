using System.Globalization;
using ServerDesk.Domain.Errors;

namespace ServerDesk.Application.RemoteFiles;

public enum RemoteExplorerUiState
{
    Disconnected,
    Loading,
    Ready,
    Empty,
    PermissionDenied,
    Error,
    Cancelled,
}

public sealed record RemoteExplorerRow(
    RemotePath Path,
    string Name,
    RemoteFileKind Kind,
    long Size,
    string SizeText,
    string ModifiedText,
    string PermissionsText,
    string OwnerText,
    string GroupText)
{
    public bool IsDirectory => Kind == RemoteFileKind.Directory;

    public bool IsDownloadable => Kind is RemoteFileKind.File or RemoteFileKind.SymbolicLink;
}

public static class RemoteExplorerProjection
{
    public static IReadOnlyList<RemoteExplorerRow> Project(IEnumerable<RemoteFileEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        return entries
            .OrderBy(entry => entry.Kind == RemoteFileKind.Directory ? 0 : 1)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .Select(entry => new RemoteExplorerRow(
                entry.Path,
                entry.Name,
                entry.Kind,
                entry.Size,
                entry.Kind == RemoteFileKind.Directory ? string.Empty : FormatBytes(entry.Size),
                entry.LastWriteTimeUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture) ?? "—",
                entry.Permissions.ToString(),
                entry.UserId?.ToString(CultureInfo.InvariantCulture) ?? "—",
                entry.GroupId?.ToString(CultureInfo.InvariantCulture) ?? "—"))
            .ToArray();
    }

    public static RemoteExplorerUiState Classify(RemoteError error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return error.Code switch
        {
            RemoteErrorCode.PermissionDenied => RemoteExplorerUiState.PermissionDenied,
            RemoteErrorCode.NetworkInterrupted or RemoteErrorCode.ConnectionFailed or RemoteErrorCode.CommandTimeout =>
                RemoteExplorerUiState.Disconnected,
            RemoteErrorCode.OperationCancelled => RemoteExplorerUiState.Cancelled,
            _ => RemoteExplorerUiState.Error,
        };
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            return "—";
        }

        string[] suffixes = ["B", "KB", "MB", "GB", "TB", "PB"];
        var value = (double)bytes;
        var suffix = 0;
        while (value >= 1024d && suffix < suffixes.Length - 1)
        {
            value /= 1024d;
            suffix++;
        }

        return suffix == 0
            ? $"{bytes.ToString("N0", CultureInfo.CurrentCulture)} B"
            : $"{value.ToString(value >= 100 ? "N0" : "N1", CultureInfo.CurrentCulture)} {suffixes[suffix]}";
    }
}
