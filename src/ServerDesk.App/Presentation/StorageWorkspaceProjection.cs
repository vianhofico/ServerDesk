using ServerDesk.Application.Storage;

namespace ServerDesk.App.Presentation;

public sealed record StorageFilesystemRow(
    string Device,
    string FileSystemType,
    string TotalText,
    string UsedText,
    string AvailableText,
    string PercentText,
    double UsedPercent,
    string MountPoint,
    bool IsWarning,
    string SearchText);

public sealed record StorageBlockDeviceRow(
    string Name,
    string ParentName,
    string Type,
    string SizeText,
    string FileSystemType,
    string MountPoint,
    string Model,
    string MediaText,
    bool IsMounted,
    string SearchText);

public sealed record StorageDirectoryRow(string Path, string SizeText, long SizeBytes);

public sealed record StorageWorkspaceSummary(
    int Filesystems,
    int VisibleFilesystems,
    int WarningFilesystems,
    int BlockDevices,
    int VisibleBlockDevices,
    int MountedBlockDevices);

public static class StorageWorkspaceProjection
{
    public const int WarningThresholdPercent = 85;

    public static IReadOnlyList<StorageFilesystemRow> ProjectFilesystems(
        IReadOnlyList<ServerFilesystemInfo> filesystems)
    {
        ArgumentNullException.ThrowIfNull(filesystems);
        return filesystems.Select(info => new StorageFilesystemRow(
                info.Device,
                info.FileSystemType,
                FormatBytes(info.TotalBytes),
                FormatBytes(info.UsedBytes),
                FormatBytes(info.AvailableBytes),
                $"{info.UsedPercent:0.#}%",
                info.UsedPercent,
                info.MountPoint,
                info.IsWarning,
                $"{info.Device} {info.FileSystemType} {info.MountPoint}"))
            .ToArray();
    }

    public static IReadOnlyList<StorageBlockDeviceRow> ProjectBlockDevices(
        IReadOnlyList<ServerBlockDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        return devices.Select(info =>
        {
            var parent = DisplayOrDash(info.ParentName);
            var fileSystemType = DisplayOrDash(info.FileSystemType);
            var mountPoint = DisplayOrDash(info.MountPoint);
            var model = DisplayOrDash(info.Model);
            var media = info.IsRotational is null ? "Unknown" : info.IsRotational.Value ? "HDD" : "SSD/flash";
            return new StorageBlockDeviceRow(
                info.Name,
                parent,
                info.Type,
                FormatBytes(info.SizeBytes),
                fileSystemType,
                mountPoint,
                model,
                media,
                !string.IsNullOrWhiteSpace(info.MountPoint),
                $"{info.Name} {info.KernelName} {parent} {info.Type} {fileSystemType} {mountPoint} {model} {media}");
        }).ToArray();
    }

    public static IReadOnlyList<StorageDirectoryRow> ProjectDirectory(
        IReadOnlyList<ServerDirectoryUsageInfo> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries.Select(info => new StorageDirectoryRow(info.Path, FormatBytes(info.SizeBytes), info.SizeBytes)).ToArray();
    }

    public static IReadOnlyList<StorageFilesystemRow> FilterFilesystems(
        IReadOnlyList<StorageFilesystemRow> rows,
        string? search) => Filter(rows, search, row => row.SearchText);

    public static IReadOnlyList<StorageBlockDeviceRow> FilterBlockDevices(
        IReadOnlyList<StorageBlockDeviceRow> rows,
        string? search) => Filter(rows, search, row => row.SearchText);

    public static StorageWorkspaceSummary Summarize(
        IReadOnlyList<StorageFilesystemRow> allFilesystems,
        IReadOnlyList<StorageFilesystemRow> visibleFilesystems,
        IReadOnlyList<StorageBlockDeviceRow> allBlockDevices,
        IReadOnlyList<StorageBlockDeviceRow> visibleBlockDevices)
    {
        ArgumentNullException.ThrowIfNull(allFilesystems);
        ArgumentNullException.ThrowIfNull(visibleFilesystems);
        ArgumentNullException.ThrowIfNull(allBlockDevices);
        ArgumentNullException.ThrowIfNull(visibleBlockDevices);

        return new StorageWorkspaceSummary(
            allFilesystems.Count,
            visibleFilesystems.Count,
            allFilesystems.Count(row => row.IsWarning),
            allBlockDevices.Count,
            visibleBlockDevices.Count,
            allBlockDevices.Count(row => row.IsMounted));
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    private static IReadOnlyList<T> Filter<T>(
        IReadOnlyList<T> rows,
        string? search,
        Func<T, string> searchText)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(searchText);
        var query = search?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            return rows.ToArray();
        }

        return rows.Where(row => searchText(row).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private static string DisplayOrDash(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
}
