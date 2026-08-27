namespace ServerDesk.Domain.Errors;

public enum RemoteErrorCode
{
    ConnectionFailed,
    AuthenticationFailed,
    HostKeyUnknown,
    HostKeyMismatch,
    PermissionDenied,
    PathNotFound,
    PathConflict,
    SudoRequired,
    CommandNotFound,
    CapabilityUnavailable,
    UnsupportedVersion,
    CommandTimeout,
    CommandFailed,
    ParseFailed,
    NetworkInterrupted,
    AmbiguousState,
    OperationCancelled
}

public sealed record RemoteError(
    RemoteErrorCode Code,
    string Message,
    string? TechnicalDetails = null);
