namespace ServerDesk.Application.Capabilities;

public enum CapabilitySupportClassification
{
    Available,
    Absent,
    Unsupported,
    PermissionDenied,
    Unknown,
}

public static class CapabilitySupportClassifier
{
    private static readonly string[] UnsupportedMarkers =
    [
        "does not provide",
        "does not support",
        "not supported",
        "unsupported",
        "not a docker command",
        "unknown command",
    ];

    public static CapabilitySupportClassification Classify(CapabilityState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Status switch
        {
            CapabilityStatus.Available => CapabilitySupportClassification.Available,
            CapabilityStatus.PermissionDenied => CapabilitySupportClassification.PermissionDenied,
            CapabilityStatus.Unknown => CapabilitySupportClassification.Unknown,
            CapabilityStatus.Unavailable when IsKnownUnsupported(state.Detail) =>
                CapabilitySupportClassification.Unsupported,
            CapabilityStatus.Unavailable => CapabilitySupportClassification.Absent,
            _ => CapabilitySupportClassification.Unknown,
        };
    }

    private static bool IsKnownUnsupported(string? detail) =>
        !string.IsNullOrWhiteSpace(detail) &&
        UnsupportedMarkers.Any(marker => detail.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
