using ServerDesk.Application.RemoteEditing;
using ServerDesk.Application.RemoteFiles;
using Xunit;

namespace ServerDesk.Tests;

public sealed class RemoteEditorTests
{
    [Fact]
    public void DiffReportsDeterministicChangedAddedAndRemovedLines()
    {
        var diff = RemoteEditorDiff.Calculate("one\ntwo\nthree", "one\nTWO\nthree\nfour");

        Assert.Equal(1, diff.ChangedLines);
        Assert.Equal(1, diff.AddedLines);
        Assert.Equal(0, diff.RemovedLines);
        Assert.Equal(2, diff.TotalChanges);
        Assert.Equal("1 changed · 1 added · 0 removed", diff.Summary);
    }

    [Fact]
    public void ValidationResolvesOnlyFilePlaceholderWithoutShellExpansion()
    {
        var validation = new RemoteEditValidationSpec("/bin/sh", ["-n", "{file}", "literal;$(no-shell)"]);
        var arguments = validation.Resolve(RemotePath.Parse("/tmp/serverdesk test.sh"));

        Assert.Equal("-n", arguments[0]);
        Assert.Equal("/tmp/serverdesk test.sh", arguments[1]);
        Assert.Equal("literal;$(no-shell)", arguments[2]);
    }

    [Fact]
    public void NoChangeDiffIsExplicit()
    {
        var diff = RemoteEditorDiff.Calculate("same\ncontent", "same\ncontent");

        Assert.Equal(0, diff.TotalChanges);
        Assert.Equal("No changes", diff.Summary);
    }
}
