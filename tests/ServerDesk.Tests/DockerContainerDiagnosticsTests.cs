using ServerDesk.Application.Docker;
using ServerDesk.Application.Remote;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class DockerContainerDiagnosticsTests
{
    private const string ContainerId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void InspectParserRedactsSensitiveEnvironmentBeforeReturningDetails()
    {
        var details = DockerContainerDiagnosticsParser.ParseInspect(InspectJson);

        Assert.Equal(ContainerId, details.Id);
        Assert.Equal("api", details.Name);
        Assert.Equal("example/api:latest", details.Image);
        Assert.True(details.State.Running);
        Assert.Equal("healthy", details.State.HealthStatus);
        Assert.Equal(2, details.Environment.Count);

        var secret = Assert.Single(details.Environment, item => item.Name == "DATABASE_PASSWORD");
        Assert.True(secret.IsSensitive);
        Assert.Equal("••••••", secret.DisplayValue);
        Assert.DoesNotContain("super-secret", details.Environment.Select(item => item.DisplayValue));

        var ordinary = Assert.Single(details.Environment, item => item.Name == "ASPNETCORE_ENVIRONMENT");
        Assert.False(ordinary.IsSensitive);
        Assert.Equal("Production", ordinary.DisplayValue);
        Assert.Single(details.Mounts);
        Assert.Single(details.Networks);
    }

    [Theory]
    [InlineData("PASSWORD")]
    [InlineData("DB_PASSWORD")]
    [InlineData("github-token")]
    [InlineData("MY_API_KEY_VALUE")]
    [InlineData("REDIS_URL")]
    [InlineData("SESSION_SECRET")]
    public void SecretLikeEnvironmentNamesAreDetected(string name)
    {
        Assert.True(DockerContainerDiagnosticsParser.IsSensitiveEnvironmentName(name));
    }

    [Theory]
    [InlineData("PATH")]
    [InlineData("ASPNETCORE_ENVIRONMENT")]
    [InlineData("TZ")]
    [InlineData("APP_PORT")]
    public void OrdinaryEnvironmentNamesRemainVisible(string name)
    {
        Assert.False(DockerContainerDiagnosticsParser.IsSensitiveEnvironmentName(name));
    }

    [Fact]
    public void InspectParserSanitizesRemoteControlCharacters()
    {
        var json = InspectJson.Replace("Production", "Prod\\u001b[31m", StringComparison.Ordinal);

        var details = DockerContainerDiagnosticsParser.ParseInspect(json);

        var ordinary = Assert.Single(details.Environment, item => item.Name == "ASPNETCORE_ENVIRONMENT");
        Assert.DoesNotContain('\u001b', ordinary.DisplayValue);
        Assert.Contains('\uFFFD', ordinary.DisplayValue);
    }

    [Fact]
    public void InspectParserFailsClosedForMalformedJson()
    {
        Assert.Throws<FormatException>(() => DockerContainerDiagnosticsParser.ParseInspect("{not-json"));
    }

    [Fact]
    public void StatsParserNormalizesPercentagesAndBinaryDecimalByteUnits()
    {
        const string json = "{\"CPUPerc\":\"12.50%\",\"MemUsage\":\"512MiB / 2GiB\",\"MemPerc\":\"25.00%\",\"NetIO\":\"1.5MB / 2kB\",\"BlockIO\":\"4KiB / 8KiB\",\"PIDs\":\"7\"}";

        var stats = DockerContainerDiagnosticsParser.ParseStats(json);

        Assert.Equal(12.5, stats.CpuPercent);
        Assert.Equal(536_870_912, stats.MemoryUsageBytes);
        Assert.Equal(2_147_483_648, stats.MemoryLimitBytes);
        Assert.Equal(25, stats.MemoryPercent);
        Assert.Equal(1_500_000, stats.NetworkInputBytes);
        Assert.Equal(2_000, stats.NetworkOutputBytes);
        Assert.Equal(4_096, stats.BlockReadBytes);
        Assert.Equal(8_192, stats.BlockWriteBytes);
        Assert.Equal(7, stats.ProcessCount);
    }

    [Fact]
    public void LogParserKeepsStreamsAsPlainTextAndOrdersByTimestamp()
    {
        var stdout = "2026-08-28T04:00:02.000000000Z second\u001b[31m\n";
        var stderr = "2026-08-28T04:00:01.000000000Z first-error\n";

        var logs = DockerContainerDiagnosticsParser.ParseLogs(stdout, stderr);

        Assert.Equal(2, logs.Count);
        Assert.Equal(DockerLogStream.Stderr, logs[0].Stream);
        Assert.Equal("first-error", logs[0].Message);
        Assert.Equal(DockerLogStream.Stdout, logs[1].Stream);
        Assert.DoesNotContain('\u001b', logs[1].Message);
        Assert.Contains('\uFFFD', logs[1].Message);
    }

    [Fact]
    public void RetentionBufferDeduplicatesSinceBoundaryAndDropsOldestRows()
    {
        var buffer = new DockerLogRetentionBuffer(2);
        var first = Entry("2026-08-28T04:00:00Z", DockerLogStream.Stdout, "one");
        var second = Entry("2026-08-28T04:00:01Z", DockerLogStream.Stdout, "two");
        var third = Entry("2026-08-28T04:00:02Z", DockerLogStream.Stderr, "three");

        buffer.AddRange([first, second]);
        buffer.AddRange([second, third]);

        Assert.Equal([second, third], buffer.Entries);
    }

    [Fact]
    public void ClientSideLogProjectionFiltersWithoutRemoteCalls()
    {
        var entries = new[]
        {
            Entry("2026-08-28T04:00:00Z", DockerLogStream.Stdout, "request completed"),
            Entry("2026-08-28T04:00:01Z", DockerLogStream.Stderr, "database timeout"),
        };

        var filtered = DockerContainerLogProjection.Filter(entries, "timeout", DockerLogStream.Stderr);

        var entry = Assert.Single(filtered);
        Assert.Equal("database timeout", entry.Message);
    }

    [Theory]
    [InlineData("aaaaaaaaaaaa")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void ContainerIdentifierAcceptsOnlyNormalizedHexIds(string value)
    {
        var normalized = DockerContainerIdentifier.Normalize(value);

        Assert.Equal(value.ToLowerInvariant(), normalized);
    }

    [Theory]
    [InlineData("api")]
    [InlineData("--help")]
    [InlineData("../../container")]
    [InlineData("aaaaaaaaaaa")]
    [InlineData("gggggggggggg")]
    public void ContainerIdentifierRejectsNamesOptionsPathsAndNonHex(string value)
    {
        Assert.Throws<ArgumentException>(() => DockerContainerIdentifier.Normalize(value));
    }

    [Fact]
    public async Task ServiceBuildsTokenizedReadOnlyCommandsAndNeverInterpolatesContainerId()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Docker", "example.invalid", 22, "dev");
        var executor = new RecordingExecutor(profile.Id, RespondToDiagnostics);
        var service = new DockerContainerDiagnosticsService(
            new SingleExecutorFactory(executor),
            DockerContainerDiagnosticsOptions.Default);

        var inspect = await service.InspectAsync(profile, ContainerId, cancellationToken);
        var stats = await service.ReadStatsAsync(profile, ContainerId, cancellationToken);
        var logs = await service.ReadLogsSinceAsync(
            profile,
            ContainerId,
            "2026-08-28T04:00:00Z",
            10,
            cancellationToken);

        Assert.True(inspect.IsSuccess, inspect.Error?.Message);
        Assert.True(stats.IsSuccess, stats.Error?.Message);
        Assert.True(logs.IsSuccess, logs.Error?.Message);
        Assert.All(executor.Commands, command => Assert.Equal("docker", command.Executable));
        Assert.All(executor.Commands, command => Assert.Equal(ServerDesk.Domain.Operations.OperationRisk.ReadOnly, command.Risk));
        Assert.Contains(executor.Commands, command => command.Arguments.SequenceEqual(["container", "inspect", "--", ContainerId]));
        Assert.Contains(executor.Commands, command => command.Arguments.SequenceEqual(["stats", "--no-stream", "--format", "{{json .}}", "--", ContainerId]));
        Assert.Contains(executor.Commands, command => command.Arguments.SequenceEqual([
            "container", "logs", "--timestamps", "--tail", "10", "--since", "2026-08-28T04:00:00Z", "--", ContainerId]));
    }

    [Fact]
    public async Task PermissionAndMissingContainerRemainTypedErrors()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Docker", "example.invalid", 22, "dev");

        var denied = new DockerContainerDiagnosticsService(
            new HandlerFactory(profile.Id, _ => Failure(1, "permission denied while trying to connect to the Docker daemon socket")),
            DockerContainerDiagnosticsOptions.Default);
        var deniedResult = await denied.ReadStatsAsync(profile, ContainerId, cancellationToken);
        Assert.Equal(RemoteErrorCode.PermissionDenied, deniedResult.Error?.Code);

        var missing = new DockerContainerDiagnosticsService(
            new HandlerFactory(profile.Id, _ => Failure(1, "Error: No such container: aaaaaaaaaaaa")),
            DockerContainerDiagnosticsOptions.Default);
        var missingResult = await missing.ReadRecentLogsAsync(profile, ContainerId, cancellationToken: cancellationToken);
        Assert.Equal(RemoteErrorCode.PathNotFound, missingResult.Error?.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5001)]
    public async Task LogCountOutsideRetentionBoundsIsRejectedBeforeRemoteExecution(int count)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Docker", "example.invalid", 22, "dev");
        var factory = new HandlerFactory(profile.Id, _ => throw new InvalidOperationException("Remote execution must not happen."));
        var service = new DockerContainerDiagnosticsService(factory, DockerContainerDiagnosticsOptions.Default);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.ReadRecentLogsAsync(profile, ContainerId, count, cancellationToken));
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task SinceTimestampRejectsOptionLikeOrMalformedInput()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Docker", "example.invalid", 22, "dev");
        var service = new DockerContainerDiagnosticsService(
            new HandlerFactory(profile.Id, _ => throw new InvalidOperationException()),
            DockerContainerDiagnosticsOptions.Default);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ReadLogsSinceAsync(
                profile,
                ContainerId,
                "--since=forever",
                cancellationToken: cancellationToken));
    }

    private static DockerContainerLogEntry Entry(string token, DockerLogStream stream, string message) =>
        new(DateTimeOffset.Parse(token, System.Globalization.CultureInfo.InvariantCulture), token, stream, message);

    private static RemoteExecutionResult RespondToDiagnostics(RemoteCommandSpec spec)
    {
        if (spec.Arguments.SequenceEqual(["container", "inspect", "--", ContainerId]))
        {
            return Success(InspectJson);
        }

        if (spec.Arguments.Count > 0 && spec.Arguments[0] == "stats")
        {
            return Success("{\"CPUPerc\":\"1%\",\"MemUsage\":\"1MiB / 2MiB\",\"MemPerc\":\"50%\",\"NetIO\":\"1kB / 2kB\",\"BlockIO\":\"3kB / 4kB\",\"PIDs\":\"1\"}\n");
        }

        if (spec.Arguments.Count > 1 && spec.Arguments[0] == "container" && spec.Arguments[1] == "logs")
        {
            return Success("2026-08-28T04:00:01Z hello\n");
        }

        return Failure(8, "unexpected fixture command");
    }

    private static RemoteExecutionResult Success(string output) =>
        RemoteExecutionResult.Success(new RemoteCommandResult(0, output, string.Empty, TimeSpan.FromMilliseconds(1)));

    private static RemoteExecutionResult Failure(int exitCode, string error) =>
        RemoteExecutionResult.Success(new RemoteCommandResult(exitCode, string.Empty, error, TimeSpan.FromMilliseconds(1)));

    private sealed class RecordingExecutor : IRemoteCommandExecutor
    {
        private readonly Func<RemoteCommandSpec, RemoteExecutionResult> _handler;

        public RecordingExecutor(Guid serverProfileId, Func<RemoteCommandSpec, RemoteExecutionResult> handler)
        {
            ServerProfileId = serverProfileId;
            _handler = handler;
        }

        public Guid ServerProfileId { get; }
        public List<RemoteCommandSpec> Commands { get; } = [];

        public Task<RemoteExecutionResult> ExecuteAsync(RemoteCommandSpec command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            return Task.FromResult(_handler(command));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SingleExecutorFactory : IRemoteCommandExecutorFactory
    {
        private readonly RecordingExecutor _executor;

        public SingleExecutorFactory(RecordingExecutor executor) => _executor = executor;

        public IRemoteCommandExecutor Create(ServerProfile profile) => _executor;
    }

    private sealed class HandlerFactory : IRemoteCommandExecutorFactory
    {
        private readonly Guid _profileId;
        private readonly Func<RemoteCommandSpec, RemoteExecutionResult> _handler;

        public HandlerFactory(Guid profileId, Func<RemoteCommandSpec, RemoteExecutionResult> handler)
        {
            _profileId = profileId;
            _handler = handler;
        }

        public int CreateCount { get; private set; }

        public IRemoteCommandExecutor Create(ServerProfile profile)
        {
            CreateCount++;
            return new RecordingExecutor(_profileId, _handler);
        }
    }

    private const string InspectJson = """
        [
          {
            "Id": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "Name": "/api",
            "Created": "2026-08-28T03:00:00Z",
            "Path": "dotnet",
            "Args": ["Api.dll"],
            "RestartCount": 1,
            "Config": {
              "Image": "example/api:latest",
              "User": "1000:1000",
              "WorkingDir": "/app",
              "Env": [
                "DATABASE_PASSWORD=super-secret",
                "ASPNETCORE_ENVIRONMENT=Production"
              ],
              "Labels": { "com.example.role": "api" }
            },
            "State": {
              "Status": "running",
              "Running": true,
              "Paused": false,
              "Restarting": false,
              "OOMKilled": false,
              "Dead": false,
              "Pid": 123,
              "ExitCode": 0,
              "StartedAt": "2026-08-28T03:00:01Z",
              "FinishedAt": "0001-01-01T00:00:00Z",
              "Health": { "Status": "healthy" }
            },
            "Mounts": [
              { "Type": "volume", "Source": "/var/lib/docker/volumes/data/_data", "Destination": "/data", "Mode": "z", "RW": true, "Propagation": "" }
            ],
            "NetworkSettings": {
              "Networks": {
                "app": { "IPAddress": "172.20.0.2", "Gateway": "172.20.0.1", "MacAddress": "02:42:ac:14:00:02" }
              }
            }
          }
        ]
        """;
}
