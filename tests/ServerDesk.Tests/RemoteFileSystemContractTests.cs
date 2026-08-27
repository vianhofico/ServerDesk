using ServerDesk.Application.RemoteFiles;
using ServerDesk.Domain.Errors;
using Xunit;

namespace ServerDesk.Tests;

public sealed class RemoteFileSystemContractTests
{
    [Theory]
    [InlineData("/var/log/../lib//app/./data", "/var/lib/app/data")]
    [InlineData("projects/alpha/../beta", "projects/beta")]
    [InlineData("/", "/")]
    [InlineData("./folder/file.txt", "folder/file.txt")]
    public void RemotePathNormalizesWithoutShellInterpretation(string input, string expected)
    {
        Assert.Equal(expected, RemotePath.Parse(input).Value);
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("../../root")]
    [InlineData("/../../root")]
    public void RemotePathRejectsTraversalAboveSuppliedRoot(string input)
    {
        Assert.Throws<ArgumentException>(() => RemotePath.Parse(input));
    }

    [Fact]
    public void RemotePathPreservesShellMetacharactersAsPlainFilenameData()
    {
        var path = RemotePath.Parse("uploads/$(touch owned); report [final].txt");

        Assert.Equal("uploads/$(touch owned); report [final].txt", path.Value);
        Assert.Equal("$(touch owned); report [final].txt", path.Name);
    }

    [Fact]
    public void CombineRequiresExactlyOnePathSegment()
    {
        var root = RemotePath.Parse("/srv/files");

        Assert.Equal("/srv/files/report.txt", root.Combine("report.txt").Value);
        Assert.Throws<ArgumentException>(() => root.Combine("../report.txt"));
        Assert.Throws<ArgumentException>(() => root.Combine("nested/report.txt"));
    }

    [Theory]
    [InlineData((short)600)]
    [InlineData((short)640)]
    [InlineData((short)755)]
    [InlineData((short)1777)]
    public void PermissionModeAcceptsUnixOctalDigits(short mode)
    {
        Assert.Equal(mode, RemoteUnixPermissions.FromMode(mode).Mode);
    }

    [Theory]
    [InlineData((short)888)]
    [InlineData((short)10000)]
    [InlineData((short)-1)]
    public void PermissionModeRejectsInvalidDigits(short mode)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RemoteUnixPermissions.FromMode(mode));
    }

    [Fact]
    public void ApplicationRemoteFilesystemBoundaryDoesNotReferenceSshNet()
    {
        var references = typeof(IRemoteFileSystem).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(references, name => name.StartsWith("Renci.SshNet", StringComparison.Ordinal));
    }

    [Fact]
    public void FilesystemHasDedicatedTypedErrors()
    {
        Assert.True(Enum.IsDefined(RemoteErrorCode.PathNotFound));
        Assert.True(Enum.IsDefined(RemoteErrorCode.PathConflict));
        Assert.True(Enum.IsDefined(RemoteErrorCode.PermissionDenied));
        Assert.True(Enum.IsDefined(RemoteErrorCode.NetworkInterrupted));
    }
}
