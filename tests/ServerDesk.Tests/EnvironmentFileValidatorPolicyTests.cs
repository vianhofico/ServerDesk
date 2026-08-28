using ServerDesk.Application.EnvironmentFiles;
using ServerDesk.Application.RemoteEditing;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class EnvironmentFileValidatorPolicyTests
{
    [Theory]
    [InlineData("python3", "{file}")]
    [InlineData("node", "{file}")]
    [InlineData("docker", "run", "--rm", "example", "{file}")]
    public async Task GenericOrExecutingValidatorShapesAreRejectedBeforeSave(string executable, params string[] arguments)
    {
        var document = Document("A=1\n");
        var editor = new CountingEditor(document);
        var service = new EnvironmentFileService(editor, EnvironmentFileOptions.Default);
        var loaded = await service.LoadAsync(Profile(), document.Metadata.Path, TestContext.Current.CancellationToken);

        var result = await service.ApplyAsync(
            Profile(),
            loaded.Snapshot!,
            "A=2\n",
            new EnvironmentFileValidationSpec(executable, arguments),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.InvalidEndpoint, result.Error?.Code);
        Assert.Equal(0, editor.SaveCalls);
    }

    [Theory]
    [InlineData("docker", "compose", "--env-file", "{file}", "config", "--quiet")]
    [InlineData("/usr/bin/docker-compose", "--env-file", "{file}", "config", "--quiet")]
    public async Task KnownNonExecutingComposeValidatorShapesAreAccepted(string executable, params string[] arguments)
    {
        var document = Document("A=1\n");
        var editor = new CountingEditor(document);
        var service = new EnvironmentFileService(editor, EnvironmentFileOptions.Default);
        var loaded = await service.LoadAsync(Profile(), document.Metadata.Path, TestContext.Current.CancellationToken);

        var result = await service.ApplyAsync(
            Profile(),
            loaded.Snapshot!,
            "A=2\n",
            new EnvironmentFileValidationSpec(executable, arguments),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(1, editor.SaveCalls);
    }

    private static RemoteEditorDocument Document(string text)
    {
        var path = RemotePath.Parse("/srv/app/.env");
        var metadata = new RemoteFileEntry(
            path,
            ".env",
            RemoteFileKind.File,
            System.Text.Encoding.UTF8.GetByteCount(text),
            new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero),
            1000,
            1000,
            RemoteUnixPermissions.FromMode(640));
        return new RemoteEditorDocument(metadata, text);
    }

    private static ServerProfile Profile() => ServerProfile.Create("env-validator", "example.invalid", 22, "dev");

    private sealed class CountingEditor : IRemoteFileEditorService
    {
        public CountingEditor(RemoteEditorDocument current) => Current = current;

        public RemoteEditorDocument Current { get; private set; }
        public int SaveCalls { get; private set; }

        public ValueTask<RemoteEditorDocument> LoadAsync(
            ServerProfile profile,
            RemotePath path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Current);
        }

        public ValueTask<RemoteEditorSaveResult> SaveWritableAsync(
            ServerProfile profile,
            RemoteEditorDocument original,
            string editedText,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<RemoteEditorSaveResult> SavePrivilegedAsync(
            ServerProfile profile,
            RemoteEditorDocument original,
            string editedText,
            RemoteEditValidationSpec? validation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveCalls++;
            Current = new RemoteEditorDocument(
                original.Metadata with
                {
                    Size = System.Text.Encoding.UTF8.GetByteCount(editedText),
                    LastWriteTimeUtc = original.Metadata.LastWriteTimeUtc?.AddMinutes(1),
                },
                editedText);
            return ValueTask.FromResult(new RemoteEditorSaveResult(true, false, "saved"));
        }
    }
}
