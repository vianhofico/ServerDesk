using System.Net;
using ServerDesk.Application.Databases;
using ServerDesk.Domain.Errors;

namespace ServerDesk.Infrastructure.Databases;

internal static class DatabaseDiagnosticAdapterUtilities
{
    public static DatabaseDiagnosticResult? ValidateRequest(
        DatabaseEngineDiagnosticRequest request,
        params DatabaseEngineKind[] supportedEngines)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IPAddress.IsLoopback(request.LocalAddress) || request.LocalPort is < 1 or > 65535)
        {
            return DatabaseDiagnosticResult.Failed(
                DatabaseDiagnosticFailureKind.NetworkFailed,
                "Database diagnostics require a valid loopback tunnel endpoint.",
                new RemoteError(RemoteErrorCode.InvalidEndpoint, "Database diagnostics endpoint was not loopback-only."));
        }

        if (!supportedEngines.Contains(request.Profile.Engine))
        {
            return DatabaseDiagnosticResult.Failed(
                DatabaseDiagnosticFailureKind.UnsupportedEngine,
                $"The diagnostic adapter does not support {request.Profile.Engine}.");
        }

        return null;
    }

    public static int TimeoutSeconds(TimeSpan value) =>
        Math.Clamp((int)Math.Ceiling(value.TotalSeconds), 1, 60);

    public static DatabaseDiagnosticResult Timeout(DatabaseEngineKind engine) =>
        DatabaseDiagnosticResult.Failed(
            DatabaseDiagnosticFailureKind.Timeout,
            $"{engine} diagnostics timed out through the SSH tunnel.",
            new RemoteError(RemoteErrorCode.CommandTimeout, "Database diagnostic timeout."));

    public static DatabaseDiagnosticResult Network(DatabaseEngineKind engine) =>
        DatabaseDiagnosticResult.Failed(
            DatabaseDiagnosticFailureKind.NetworkFailed,
            $"{engine} could not be reached through the SSH tunnel.",
            new RemoteError(RemoteErrorCode.ConnectionFailed, "Database diagnostic network failure."));

    public static DatabaseDiagnosticResult Authentication(DatabaseEngineKind engine) =>
        DatabaseDiagnosticResult.Failed(
            DatabaseDiagnosticFailureKind.AuthenticationFailed,
            $"{engine} rejected the stored database credential.",
            new RemoteError(RemoteErrorCode.AuthenticationFailed, "Database authentication failed."));

    public static DatabaseDiagnosticResult Authorization(DatabaseEngineKind engine) =>
        DatabaseDiagnosticResult.Failed(
            DatabaseDiagnosticFailureKind.AuthorizationDenied,
            $"{engine} authenticated successfully but denied a required read-only diagnostic operation.",
            new RemoteError(RemoteErrorCode.PermissionDenied, "Database diagnostic permission denied."));

    public static DatabaseDiagnosticResult Parse(DatabaseEngineKind engine) =>
        DatabaseDiagnosticResult.Failed(
            DatabaseDiagnosticFailureKind.ParseFailed,
            $"{engine} returned diagnostic data that ServerDesk could not normalize.",
            new RemoteError(RemoteErrorCode.ParseFailed, "Database diagnostic parse failure."));
}
