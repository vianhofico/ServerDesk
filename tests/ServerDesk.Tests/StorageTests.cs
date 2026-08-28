using ServerDesk.Application.Storage;
using Xunit;

namespace ServerDesk.Tests;

public sealed class StorageTests
{
    [Fact]
    public void FilesystemParserPreservesBytesMountsAndWarningThreshold()
    {
        const string output =
            "Filesystem Type 1-blocks Used Available Capacity Mounted on\n" +
            "/dev/sda1 ext4 1000000 900000 100000 90% /\n" +
            "tmpfs tmpfs 500000 1000 499000 1% /run/user/1000\n";

        var rows = StorageParser.ParseFilesystems(output);

        Assert.Equal(2, rows.Count);
        Assert.Equal(1_000_000, rows[0].TotalBytes);
        Assert.Equal(900_000, rows[0].UsedBytes);
        Assert.Equal("/", rows[0].MountPoint);
        Assert.True(rows[0].IsWarning);
        Assert.False(rows[1].IsWarning);
    }

    [Fact]
    public void BlockDeviceParserFlattensChildrenAndPreservesParentAndMedia()
    {
        const string json = """
            {
              "blockdevices": [
                {
                  "name": "sda",
                  "kname": "sda",
                  "type": "disk",
                  "size": 1000000,
                  "fstype": null,
                  "mountpoint": null,
                  "model": "Fixture Disk",
                  "rota": true,
                  "children": [
                    {
                      "name": "sda1",
                      "kname": "sda1",
                      "type": "part",
                      "size": "900000",
                      "fstype": "ext4",
                      "mountpoint": "/",
                      "model": null,
                      "rota": 1
                    }
                  ]
                }
              ]
            }
            """;

        var rows = StorageParser.ParseBlockDevices(json);

        Assert.Equal(2, rows.Count);
        Assert.Equal("sda", rows[1].ParentName);
        Assert.Equal("/", rows[1].MountPoint);
        Assert.True(rows[0].IsRotational);
        Assert.True(rows[1].IsRotational);
    }

    [Fact]
    public void DirectoryUsageParserSupportsPathsWithSpacesAndSortsLargestFirst()
    {
        const string output =
            "1024\t/var/small folder\n" +
            "4096 /var/larger folder\n";

        var rows = StorageParser.ParseDirectoryUsage(output);

        Assert.Equal(2, rows.Count);
        Assert.Equal("/var/larger folder", rows[0].Path);
        Assert.Equal(4096, rows[0].SizeBytes);
        Assert.Equal("/var/small folder", rows[1].Path);
    }

    [Fact]
    public void MalformedFilesystemRowFailsClosed()
    {
        Assert.Throws<FormatException>(() => StorageParser.ParseFilesystems("/dev/sda1 ext4 not-a-size"));
    }

    [Fact]
    public void InvalidBlockJsonFailsClosed()
    {
        Assert.Throws<FormatException>(() => StorageParser.ParseBlockDevices("{ not-json"));
    }

    [Fact]
    public void MalformedDirectoryUsageFailsClosed()
    {
        Assert.Throws<FormatException>(() => StorageParser.ParseDirectoryUsage("not-a-size /var"));
    }
}
