using ServerDesk.Application.Logs;
using ServerDesk.Application.Remote;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class LogTests
{
    [Theory]
    [InlineData("ubuntu-24.04-journal.jsonl")]
    [InlineData("ubuntu-26.04-journal.jsonl")]
    [InlineData("debian-13-journal.jsonl")]
    public void CertifiedJournalFixturesParseStructuredFields(string fixtureName)
    {
        var output = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Logs", fixtureName));

        var entries = LinuxLogParser.ParseJournalJsonLines(output);

        Assert.NotEmpty(entries);
        Assert.All(entries, entry => Assert.Equal(ServerLogSource.Journal, entry.Source));
        Assert.Contains(entries, entry => !string.IsNullOrWhiteSpace(entry.Cursor));
        Assert.Contains(entries, entry => entry.Timestamp is not null);
    }

    [Fact]
    public void JournalParserAllowsMissingOptionalFields()
    {
        var entry = Assert.Single(LinuxLogParser.ParseJournalJsonLines("{\"MESSAGE\":\"hello\"}\n"));

        Assert.Equal("hello", entry.Message);
        Assert.Null(entry.Timestamp);
        Assert.Null(entry.Cursor);
        Assert.Null(entry.ProcessId);
        Assert.Equal(LogSeverity.Unknown, entry.Severity);
        Assert.Equal(string.Empty, entry.Identifier);
    }

    [Fact]
    public void JournalParserFailsClosedOnMalformedJson()
    {
        Assert.Throws<FormatException>(() => LinuxLogParser.ParseJournalJsonLines("{not-json}\n"));
    }

    [Fact]
    public void JournalParserExtractsCursorTimestampSeverityAndPid()
    {
        const string output = "{\"__REALTIME_TIMESTAMP\":\"1760000000123456\",\"__CURSOR\":\"s=fixture;i=42\",\"PRIORITY\":\"3\",\"MESSAGE\":\"failure\",\"SYSLOG_IDENTIFIER\":\"fixture\",\"_SYSTEMD_UNIT\":\"fixture.service\",\"_PID\":\"4242\",\"_HOSTNAME\":\"fixture-host\"}\n";

        var entry = Assert.Single(LinuxLogParser.ParseJournalJsonLines(output));

        Assert.Equal("s=fixture;i=42", entry.Cursor);
        Assert.Equal(LogSeverity.Error, entry.Severity);
        Assert.Equal(4242, entry.ProcessId);
        Assert.Equal("fixture.service", entry.SystemdUnit);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddTicks(17_600_000_001_234_560), entry.Timestamp);
    }

    [Fact]
    public void ControlCharactersAreSanitizedWithoutTreatingContentAsMarkup()
    {
        var entry = Assert.Single(LinuxLogParser.ParseJournalJsonLines("{\"MESSAGE\":\"normal \\u001b[31m<error>\\nnext\"}\n"));

        Assert.Equal("normal �[31m<error> next", entry.Message);
        Assert.DoesNotContain('\u001b', entry.Message);
    }

    [Fact]
    public void FileTailNormalizesLinesAsReadOnlyFileEntries()
    {
        var entries = LinuxLogParser.ParseFileTail("first\nsecond\n");

        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.Equal(ServerLogSource.File, entry.Source));
        Assert.Equal("first", entries[0].Message);
        Assert.Equal(LogSeverity.Unknown, entries[0].Severity);
    }

    [Fact]
    public void RetentionDropsOldestRowsAtConfiguredBound()
    {
        var buffer = new LogRetentionBuffer(3);
        buffer.AddRange(Enumerable.Range(1, 5).Select(index => Entry($"row-{index}")));

        Assert.Equal(["row-3", "row-4", "row-5"], buffer.Entries.Select(entry => entry.Message));
    }

    [Fact]
    public void ProjectionFiltersClientSideByTextSeverityIdentifierUnitAndSource()
    {
        var entries = new[]
        {
            Entry("nginx started", LogSeverity.Info, "nginx", "nginx.service", ServerLogSource.Journal),
            Entry("worker failed", LogSeverity.Error, "worker", "worker.service", ServerLogSource.Journal),
            Entry("worker failed in file", LogSeverity.Unknown, source: ServerLogSource.File),
        };

        var rows = ServerLogProjection.Filter(
            entries,
            new ServerLogFilter("failed", LogSeverity.Error, "work", "worker.service", ServerLogSource.Journal));

        Assert.Single(rows);
        Assert.Equal("worker failed", rows[0].Message);
    }

    [Fact]
    public async Task JournalServiceUsesTokenizedUnitAndCursorArguments()
    {
        var factory = new CaptureExecutorFactory((_, _) => Success(
            "{\"__CURSOR\":\"cursor-2\",\"MESSAGE\":\"next\",\"PRIORITY\":\"6\"}\n"));
        var service = new ServerLogService(factory, ServerLogOptions.Default);

        var result = await service.ReadJournalAfterCursorAsync(
            Profile(),
            "cursor-1",
            25,
            "nginx.service",
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("cursor-2", result.LastCursor);
        var command = Assert.Single(factory.Commands);
        Assert.Equal("journalctl", command.Executable);
        Assert.Equal(OperationRisk.ReadOnly, command.Risk);
        Assert.Contains("--after-cursor", command.Arguments);
        Assert.Contains("cursor-1", command.Arguments);
        Assert.Contains("--unit", command.Arguments);
        Assert.Contains("nginx.service", command.Arguments);
        Assert.DoesNotContain(command.Arguments, argument => argument.Contains("sh -c", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FileTailUsesNormalizedPathAsSingleArgumentAndMapsNotFound()
    {
        var factory = new CaptureExecutorFactory((_, _) => Task.FromResult(RemoteExecutionResult.Success(
            new RemoteCommandResult(1, string.Empty, "tail: cannot open '/var/log/missing.log': No such file or directory", TimeSpan.Zero))));
        var service = new ServerLogService(factory, ServerLogOptions.Default);

        var result = await service.ReadFileTailAsync(
            Profile(),
            "/var/log/app/../missing.log",
            50,
            TestContext.Current.CancellationToken);

        Assert.Equal(RemoteErrorCode.PathNotFound, result.Error?.Code);
        var command = Assert.Single(factory.Commands);
        Assert.Equal("tail", command.Executable);
        Assert.Equal("/var/log/missing.log", command.Arguments[^1]);
        Assert.Equal("--", command.Arguments[^2]);
    }

    [Theory]
    [InlineData("relative.log")]
    [InlineData("../escape.log")]
    public async Task InvalidLogPathIsRejectedBeforeRemoteExecution(string path)
    {
        var factory = new CaptureExecutorFactory((_, _) => Success(string.Empty));
        var service = new ServerLogService(factory, ServerLogOptions.Default);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ReadFileTailAsync(Profile(), path));
        Assert.Empty(factory.Commands);
    }

    [Fact]
    public async Task CancellationIsReturnedAsTypedRemoteError()
    {
        var factory = new CaptureExecutorFactory((_, _) => Task.FromResult(RemoteExecutionResult.Failure(
            new RemoteError(RemoteErrorCode.OperationCancelled, "cancelled"))));
        var service = new ServerLogService(factory, ServerLogOptions.Default);

        var result = await service.ReadJournalAsync(Profile(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RemoteErrorCode.OperationCancelled, result.Error?.Code);
    }

    [Fact]
    public async Task MalformedJournalFromRemoteBecomesTypedParseFailure()
    {
        var factory = new CaptureExecutorFactory((_, _) => Success("not-json\n"));
        var service = new ServerLogService(factory, ServerLogOptions.Default);

        var result = await service.ReadJournalAsync(Profile(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(RemoteErrorCode.ParseFailed, result.Error?.Code);
    }

    [Fact]
    public async Task UnsafeUnitFilterIsRejectedBeforeExecution()
    {
        var factory = new CaptureExecutorFactory((_, _) => Success(string.Empty));
        var service = new ServerLogService(factory, ServerLogOptions.Default);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ReadJournalAsync(Profile(), unit: "nginx.service;reboot"));
        Assert.Empty(factory.Commands);
    }

    private static LogEntry Entry(
        string message,
        LogSeverity severity = LogSeverity.Info,
        string identifier = "fixture",
        string unit = "fixture.service",
        ServerLogSource source = ServerLogSource.Journal) =>
        new(DateTimeOffset.UnixEpoch, "cursor", severity, message, identifier, unit, 1, "host", source);

    private static ServerProfile Profile() => ServerProfile.Create("Logs", "example.test", 22, "tester");

    private static Task<RemoteExecutionResult> Success(string output) => Task.FromResult(RemoteExecutionResult.Success(
        new RemoteCommandResult(0, output, string.Empty, TimeSpan.Zero)));

    private sealed class CaptureExecutorFactory : IRemoteCommandExecutorFactory
    {
        private readonly Func<RemoteCommandSpec, CancellationToken, Task<RemoteExecutionResult>> _handler;

        public CaptureExecutorFactory(Func<RemoteCommandSpec, CancellationToken, Task<RemoteExecutionResult>> handler)
        {
            _handler = handler;
        }

        public List<RemoteCommandSpec> Commands { get; } = [];

        public IRemoteCommandExecutor Create(ServerProfile profile) => new CaptureExecutor(profile.Id, Commands, _handler);
    }

    private sealed class CaptureExecutor : IRemoteCommandExecutor
    {
        private readonly List<RemoteCommandSpec> _commands;
        private readonly Func<RemoteCommandSpec, CancellationToken, Task<RemoteExecutionResult>> _handler;

        public CaptureExecutor(
            Guid serverProfileId,
            List<RemoteCommandSpec> commands,
            Func<RemoteCommandSpec, CancellationToken, Task<RemoteExecutionResult>> handler)
        {
            ServerProfileId = serverProfileId;
            _commands = commands;
            _handler = handler;
        }

        public Guid ServerProfileId { get; }

        public Task<RemoteExecutionResult> ExecuteAsync(RemoteCommandSpec command, CancellationToken cancellationToken = default)
        {
            _commands.Add(command);
            return _handler(command, cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
