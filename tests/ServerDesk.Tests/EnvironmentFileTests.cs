using ServerDesk.Application.Audit;
using ServerDesk.Application.EnvironmentFiles;
using ServerDesk.Application.RemoteEditing;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class EnvironmentFileTests
{
    [Fact]
    public void SimpleEditPreservesCommentsOrderAndUnsupportedLines()
    {
        const string original = "# deployment\r\nAPP_URL=https://example.test\r\nexport LEGACY=value\r\nPASSWORD =  old-secret\r\n# tail\r\n";

        var parsed = EnvironmentFileParser.Parse(original);
        var password = Assert.Single(parsed.Entries, entry => entry.Key == "PASSWORD");
        var edited = EnvironmentFileEditor.SetValueAtLine(original, password.LineNumber, password.Key, "new-secret");

        Assert.Equal(
            "# deployment\r\nAPP_URL=https://example.test\r\nexport LEGACY=value\r\nPASSWORD =  new-secret\r\n# tail\r\n",
            edited);
        Assert.True(parsed.HasUnsupportedLines);
        Assert.Equal(EnvironmentFileLineKind.Unsupported, parsed.Lines[2].Kind);
    }

    [Theory]
    [InlineData("DATABASE_PASSWORD", "ordinary", true)]
    [InlineData("PUBLIC_VALUE", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJzZXJ2ZXJkZXNrIn0.qwertyuiopasdfghjkl", true)]
    [InlineData("PUBLIC_URL", "https://example.test", false)]
    [InlineData("CONNECTION", "postgres://user:secret@example.test/db", true)]
    public void SecretClassifierMasksSecretLookingNamesAndValues(string key, string value, bool expected)
    {
        var secret = EnvironmentSecretClassifier.IsSecret(key, value);
        var entry = new EnvironmentFileEntry(1, key, value, secret);

        Assert.Equal(expected, secret);
        Assert.Equal(expected ? EnvironmentSecretClassifier.Mask : value, EnvironmentSecretClassifier.DisplayValue(entry, revealed: false));
        Assert.Equal(value, EnvironmentSecretClassifier.DisplayValue(entry, revealed: true));
    }

    [Fact]
    public async Task ConcurrentRemoteChangeBlocksOverwriteBeforeMutation()
    {
        var original = Document("A=1\n", lastWriteMinute: 1);
        var editor = new FakeEditor(original);
        var service = new EnvironmentFileService(editor, EnvironmentFileOptions.Default);
        var loaded = await service.LoadAsync(Profile(), Path(), TestContext.Current.CancellationToken);
        Assert.True(loaded.IsSuccess, loaded.Error?.Message);
        editor.Current = Document("A=remote-change\n", lastWriteMinute: 2);

        var result = await service.ApplyAsync(
            Profile(),
            loaded.Snapshot!,
            "A=local-change\n",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.PathConflict, result.Error?.Code);
        Assert.Equal(0, editor.SaveCalls);
    }

    [Fact]
    public async Task ApplyUsesPrivilegedAtomicEditorAndReturnsVerifiedSnapshot()
    {
        var editor = new FakeEditor(Document("A=1\n", lastWriteMinute: 1));
        var service = new EnvironmentFileService(editor, EnvironmentFileOptions.Default);
        var loaded = await service.LoadAsync(Profile(), Path(), TestContext.Current.CancellationToken);

        var result = await service.ApplyAsync(
            Profile(),
            loaded.Snapshot!,
            "A=2\n",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(1, editor.SaveCalls);
        Assert.Equal("A=2\n", result.Snapshot!.Text);
        Assert.Equal(640, result.Snapshot.Original.Metadata.Permissions.Mode);
        Assert.Equal(1000, result.Snapshot.Original.Metadata.UserId);
        Assert.Equal(1000, result.Snapshot.Original.Metadata.GroupId);
    }

    [Fact]
    public async Task ShellValidatorIsRejectedWithoutExecutingOrSavingEnvContent()
    {
        var editor = new FakeEditor(Document("SECRET=value\n", lastWriteMinute: 1));
        var service = new EnvironmentFileService(editor, EnvironmentFileOptions.Default);
        var loaded = await service.LoadAsync(Profile(), Path(), TestContext.Current.CancellationToken);

        var result = await service.ApplyAsync(
            Profile(),
            loaded.Snapshot!,
            "SECRET=changed\n",
            new EnvironmentFileValidationSpec("/bin/sh", ["{file}"]),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.InvalidEndpoint, result.Error?.Code);
        Assert.Equal(0, editor.SaveCalls);
    }

    [Fact]
    public async Task ValidatorGetsOnlyTypedStagedFilePlaceholder()
    {
        var editor = new FakeEditor(Document("A=1\n", lastWriteMinute: 1));
        var service = new EnvironmentFileService(editor, EnvironmentFileOptions.Default);
        var loaded = await service.LoadAsync(Profile(), Path(), TestContext.Current.CancellationToken);

        var result = await service.ApplyAsync(
            Profile(),
            loaded.Snapshot!,
            "A=2\n",
            new EnvironmentFileValidationSpec("docker", ["compose", "--env-file", "{file}", "config", "--quiet"]),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Message);
        Assert.NotNull(editor.LastValidation);
        Assert.Equal("docker", editor.LastValidation!.Executable);
        Assert.Contains("{file}", editor.LastValidation.Arguments);
    }

    [Fact]
    public async Task AmbiguousSaveIsNotRetried()
    {
        var editor = new FakeEditor(Document("A=1\n", lastWriteMinute: 1))
        {
            SaveResult = new RemoteEditorSaveResult(
                false,
                false,
                "transport lost",
                new RemoteError(RemoteErrorCode.AmbiguousState, "transport lost")),
        };
        var service = new EnvironmentFileService(editor, EnvironmentFileOptions.Default);
        var loaded = await service.LoadAsync(Profile(), Path(), TestContext.Current.CancellationToken);

        var result = await service.ApplyAsync(
            Profile(),
            loaded.Snapshot!,
            "A=2\n",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.True(result.AmbiguousState);
        Assert.Equal(1, editor.SaveCalls);
    }

    [Fact]
    public async Task AuditContainsPathAndOutcomeButNeverCandidateValues()
    {
        var audit = new MemoryAudit();
        var inner = new SuccessfulEnvironmentFileService();
        var service = new AuditedEnvironmentFileService(inner, audit);
        var snapshot = Snapshot("PASSWORD=old-secret\n");

        _ = await service.ApplyAsync(
            Profile(),
            snapshot,
            "PASSWORD=ultra-secret-value\n",
            cancellationToken: TestContext.Current.CancellationToken);

        var entry = Assert.Single(audit.Entries);
        var persisted = string.Join('|', entry.Category, entry.Summary, entry.Target);
        Assert.DoesNotContain("old-secret", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("ultra-secret-value", persisted, StringComparison.Ordinal);
        Assert.Equal(OperationRisk.Mutating, entry.Risk);
        Assert.Equal(OperationOutcome.Succeeded, entry.Outcome);
    }

    private static EnvironmentFileSnapshot Snapshot(string text)
    {
        var document = Document(text, lastWriteMinute: 1);
        var parsed = EnvironmentFileParser.Parse(text);
        return new EnvironmentFileSnapshot(
            Path(),
            document,
            parsed.Lines,
            parsed.Entries,
            parsed.HasUnsupportedLines,
            parsed.NewLine);
    }

    private static RemoteEditorDocument Document(string text, int lastWriteMinute)
    {
        var bytes = System.Text.Encoding.UTF8.GetByteCount(text);
        var metadata = new RemoteFileEntry(
            Path(),
            ".env",
            RemoteFileKind.File,
            bytes,
            new DateTimeOffset(2026, 8, 28, 12, lastWriteMinute, 0, TimeSpan.Zero),
            1000,
            1000,
            RemoteUnixPermissions.FromMode(640));
        return new RemoteEditorDocument(metadata, text);
    }

    private static RemotePath Path() => RemotePath.Parse("/srv/app/.env");

    private static ServerProfile Profile() => ServerProfile.Create("env", "example.invalid", 22, "dev");

    private sealed class FakeEditor : IRemoteFileEditorService
    {
        public FakeEditor(RemoteEditorDocument current) => Current = current;

        public RemoteEditorDocument Current { get; set; }
        public int SaveCalls { get; private set; }
        public RemoteEditValidationSpec? LastValidation { get; private set; }
        public RemoteEditorSaveResult SaveResult { get; set; } = new(true, false, "saved");

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
            throw new InvalidOperationException("Environment-file apply must use the privileged atomic editor path.");

        public ValueTask<RemoteEditorSaveResult> SavePrivilegedAsync(
            ServerProfile profile,
            RemoteEditorDocument original,
            string editedText,
            RemoteEditValidationSpec? validation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveCalls++;
            LastValidation = validation;
            if (!SaveResult.IsSuccess)
            {
                return ValueTask.FromResult(SaveResult);
            }

            var bytes = System.Text.Encoding.UTF8.GetByteCount(editedText);
            Current = new RemoteEditorDocument(
                original.Metadata with
                {
                    Size = bytes,
                    LastWriteTimeUtc = original.Metadata.LastWriteTimeUtc?.AddMinutes(1),
                },
                editedText);
            return ValueTask.FromResult(SaveResult);
        }
    }

    private sealed class MemoryAudit : IOperationAudit
    {
        public List<OperationAuditEntry> Entries { get; } = [];

        public ValueTask AppendAsync(OperationAuditEntry entry, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Entries.Add(entry);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<OperationAuditEntry>> ListRecentAsync(
            int limit,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<OperationAuditEntry>>(Entries.Take(limit).ToArray());
    }

    private sealed class SuccessfulEnvironmentFileService : IEnvironmentFileService
    {
        public ValueTask<EnvironmentFileLoadResult> LoadAsync(
            ServerProfile profile,
            RemotePath path,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<EnvironmentFileApplyResult> ApplyAsync(
            ServerProfile profile,
            EnvironmentFileSnapshot original,
            string candidateText,
            EnvironmentFileValidationSpec? validation = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new EnvironmentFileApplyResult(true, false, false, "applied", Snapshot: original));
        }
    }
}
