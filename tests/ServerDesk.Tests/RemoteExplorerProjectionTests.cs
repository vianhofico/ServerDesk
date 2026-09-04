using ServerDesk.Application.RemoteFiles;
using ServerDesk.Domain.Errors;
using Xunit;

namespace ServerDesk.Tests;

public sealed class RemoteExplorerProjectionTests
{
    [Fact]
    public void ProjectSortsDirectoriesFirstAndPreservesMetadata()
    {
        var timestamp = new DateTimeOffset(2026, 8, 27, 12, 30, 0, TimeSpan.Zero);
        RemoteFileEntry[] entries =
        [
            new(
                RemotePath.Parse("/srv/z.log"),
                "z.log",
                RemoteFileKind.File,
                1536,
                timestamp,
                1000,
                1001,
                RemoteUnixPermissions.FromMode(640)),
            new(
                RemotePath.Parse("/srv/app"),
                "app",
                RemoteFileKind.Directory,
                0,
                timestamp,
                1000,
                1001,
                RemoteUnixPermissions.FromMode(750)),
            new(
                RemotePath.Parse("/srv/A.txt"),
                "A.txt",
                RemoteFileKind.File,
                10,
                timestamp,
                null,
                null,
                RemoteUnixPermissions.FromMode(600)),
        ];

        var rows = RemoteExplorerProjection.Project(entries);

        Assert.Equal(["app", "A.txt", "z.log"], rows.Select(row => row.Name));
        Assert.Equal(string.Empty, rows[0].SizeText);
        Assert.Equal("640", rows[2].PermissionsText);
        Assert.Equal("1000", rows[2].OwnerText);
        Assert.Equal("1001", rows[2].GroupText);
        Assert.True(rows[0].IsDirectory);
        Assert.True(rows[2].IsDownloadable);
    }

    [Fact]
    public void FilterMatchesLoadedRowsWithoutChangingRemoteOrder()
    {
        var timestamp = new DateTimeOffset(2026, 8, 27, 12, 30, 0, TimeSpan.Zero);
        var rows = RemoteExplorerProjection.Project(
        [
            new RemoteFileEntry(
                RemotePath.Parse("/srv/logs"),
                "logs",
                RemoteFileKind.Directory,
                0,
                timestamp,
                1000,
                1001,
                RemoteUnixPermissions.FromMode(750)),
            new RemoteFileEntry(
                RemotePath.Parse("/srv/app.log"),
                "app.log",
                RemoteFileKind.File,
                10,
                timestamp,
                2000,
                2001,
                RemoteUnixPermissions.FromMode(640)),
        ]);

        Assert.Equal(["app.log"], RemoteExplorerProjection.Filter(rows, "APP").Select(row => row.Name));
        Assert.Equal(["logs"], RemoteExplorerProjection.Filter(rows, "750").Select(row => row.Name));
        Assert.Equal(["app.log"], RemoteExplorerProjection.Filter(rows, "2000").Select(row => row.Name));
        Assert.Equal(rows.Select(row => row.Name), RemoteExplorerProjection.Filter(rows, " ").Select(row => row.Name));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1048576, "1.0 MB")]
    public void FormatBytesUsesStableBinaryScale(long bytes, string expected)
    {
        Assert.Equal(expected, RemoteExplorerProjection.FormatBytes(bytes));
    }

    [Theory]
    [InlineData(RemoteErrorCode.PermissionDenied, RemoteExplorerUiState.PermissionDenied)]
    [InlineData(RemoteErrorCode.NetworkInterrupted, RemoteExplorerUiState.Disconnected)]
    [InlineData(RemoteErrorCode.ConnectionFailed, RemoteExplorerUiState.Disconnected)]
    [InlineData(RemoteErrorCode.OperationCancelled, RemoteExplorerUiState.Cancelled)]
    [InlineData(RemoteErrorCode.PathConflict, RemoteExplorerUiState.Error)]
    public void ClassifyMapsTypedRemoteErrorsToExplicitUiStates(
        RemoteErrorCode code,
        RemoteExplorerUiState expected)
    {
        Assert.Equal(expected, RemoteExplorerProjection.Classify(new RemoteError(code, "test")));
    }
}
