using ServerDesk.App.Presentation;
using ServerDesk.Application.Storage;
using Xunit;

namespace ServerDesk.Tests;

public sealed class StorageWorkspaceProjectionTests
{
    [Fact]
    public void ProjectFilesystemsKeepsCertifiedWarningThresholdAndTechnicalValues()
    {
        var rows = StorageWorkspaceProjection.ProjectFilesystems(
        [
            new ServerFilesystemInfo("/dev/sda1", "ext4", 100 * 1024L, 84 * 1024L, 16 * 1024L, 84.9, "/"),
            new ServerFilesystemInfo("/dev/sdb1", "xfs", 200 * 1024L, 170 * 1024L, 30 * 1024L, 85.0, "/data"),
        ]);

        Assert.Equal(85, StorageWorkspaceProjection.WarningThresholdPercent);
        Assert.False(rows[0].IsWarning);
        Assert.True(rows[1].IsWarning);
        Assert.Equal("100 KiB", rows[0].TotalText);
        Assert.Equal("85%", rows[1].PercentText);
        Assert.Equal("/data", rows[1].MountPoint);
    }

    [Fact]
    public void FiltersRemainLocalAndMatchFilesystemAndBlockDeviceMetadata()
    {
        var filesystems = StorageWorkspaceProjection.ProjectFilesystems(
        [
            new ServerFilesystemInfo("/dev/sda1", "ext4", 1, 1, 0, 90, "/"),
            new ServerFilesystemInfo("tmpfs", "tmpfs", 1, 1, 0, 10, "/run"),
        ]);
        var blocks = StorageWorkspaceProjection.ProjectBlockDevices(
        [
            new ServerBlockDeviceInfo("sda", "sda", "disk", 1024, "", "", "Samsung SSD", false, null),
            new ServerBlockDeviceInfo("sda1", "sda1", "part", 512, "ext4", "/", "", null, "sda"),
        ]);

        Assert.Equal(["/dev/sda1"], StorageWorkspaceProjection.FilterFilesystems(filesystems, "EXT4").Select(row => row.Device));
        Assert.Equal(["tmpfs"], StorageWorkspaceProjection.FilterFilesystems(filesystems, "/RUN").Select(row => row.Device));
        Assert.Equal(["sda"], StorageWorkspaceProjection.FilterBlockDevices(blocks, "samsung").Select(row => row.Name));
        Assert.Equal(["sda1"], StorageWorkspaceProjection.FilterBlockDevices(blocks, "sda1").Select(row => row.Name));
    }

    [Fact]
    public void SummaryUsesLoadedSnapshotTotalsAndVisibleFilterCounts()
    {
        var filesystems = StorageWorkspaceProjection.ProjectFilesystems(
        [
            new ServerFilesystemInfo("/dev/a", "ext4", 1, 1, 0, 90, "/a"),
            new ServerFilesystemInfo("/dev/b", "xfs", 1, 1, 0, 20, "/b"),
        ]);
        var blocks = StorageWorkspaceProjection.ProjectBlockDevices(
        [
            new ServerBlockDeviceInfo("a", "a", "disk", 1, "ext4", "/a", "A", false, null),
            new ServerBlockDeviceInfo("b", "b", "disk", 1, "", "", "B", true, null),
        ]);

        var summary = StorageWorkspaceProjection.Summarize(
            filesystems,
            StorageWorkspaceProjection.FilterFilesystems(filesystems, "/a"),
            blocks,
            StorageWorkspaceProjection.FilterBlockDevices(blocks, "A"));

        Assert.Equal(2, summary.Filesystems);
        Assert.Equal(1, summary.VisibleFilesystems);
        Assert.Equal(1, summary.WarningFilesystems);
        Assert.Equal(2, summary.BlockDevices);
        Assert.Equal(1, summary.VisibleBlockDevices);
        Assert.Equal(1, summary.MountedBlockDevices);
    }

    [Fact]
    public void DirectoryProjectionPreservesReadOnlyResultOrderAndRawSize()
    {
        var rows = StorageWorkspaceProjection.ProjectDirectory(
        [
            new ServerDirectoryUsageInfo("/var/lib", 2048),
            new ServerDirectoryUsageInfo("/var/log", 1024),
        ]);

        Assert.Equal(["/var/lib", "/var/log"], rows.Select(row => row.Path));
        Assert.Equal(2048, rows[0].SizeBytes);
        Assert.Equal("2 KiB", rows[0].SizeText);
    }
}
