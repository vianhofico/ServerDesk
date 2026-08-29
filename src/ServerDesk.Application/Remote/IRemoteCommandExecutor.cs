using System.Buffers;
using System.Text;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Remote;

public sealed class SensitiveCommandInput
{
    private const string RedactedDisplay = "<redacted-sensitive-input>";
    private readonly string _value;

    public SensitiveCommandInput(string value)
    {
        _value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public async ValueTask WriteToAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var maximumBytes = Encoding.UTF8.GetMaxByteCount(_value.Length);
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, maximumBytes));
        try
        {
            var count = Encoding.UTF8.GetBytes(_value.AsSpan(), buffer.AsSpan());
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            buffer.AsSpan().Clear();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public override string ToString() => RedactedDisplay;
}

public sealed record RemoteCommandSpec(
    string Executable,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout,
    OperationRisk Risk = OperationRisk.ReadOnly,
    IReadOnlyDictionary<string, string>? Environment = null,
    string? WorkingDirectory = null,
    SensitiveCommandInput? StandardInput = null)
{
    public static RemoteCommandSpec ReadOnly(
        string executable,
        params string[] arguments) =>
        new(executable, arguments, TimeSpan.FromSeconds(15));
}

public sealed record RemoteCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration);

public sealed record RemoteExecutionResult(
    RemoteCommandResult? Command,
    RemoteError? Error)
{
    public bool IsSuccess => Command is not null && Error is null;

    public static RemoteExecutionResult Success(RemoteCommandResult command) => new(command, null);

    public static RemoteExecutionResult Failure(RemoteError error) => new(null, error);
}

public interface IRemoteCommandExecutor : IAsyncDisposable
{
    Guid ServerProfileId { get; }

    Task<RemoteExecutionResult> ExecuteAsync(
        RemoteCommandSpec command,
        CancellationToken cancellationToken = default);
}

public interface IRemoteCommandExecutorFactory
{
    IRemoteCommandExecutor Create(ServerProfile profile);
}
