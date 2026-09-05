using ServerDesk.Application.Services;

namespace ServerDesk.App.Presentation;

public sealed record ServiceWorkspaceSummary(
    int TotalServices,
    int VisibleServices,
    int ActiveServices,
    int EnabledServices);

public static class ServiceWorkspaceProjection
{
    public static IReadOnlyList<ServerServiceInfo> Filter(
        IReadOnlyList<ServerServiceInfo> services,
        string? query)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(query))
        {
            return services;
        }

        var normalized = query.Trim();
        return services
            .Where(service => SearchText(service).Contains(normalized, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public static ServiceWorkspaceSummary Summarize(
        IReadOnlyCollection<ServerServiceInfo> allServices,
        IReadOnlyCollection<ServerServiceInfo> visibleServices)
    {
        ArgumentNullException.ThrowIfNull(allServices);
        ArgumentNullException.ThrowIfNull(visibleServices);
        return new ServiceWorkspaceSummary(
            allServices.Count,
            visibleServices.Count,
            allServices.Count(service => service.IsActive),
            allServices.Count(IsEnabled));
    }

    public static bool IsEnabled(ServerServiceInfo service)
    {
        ArgumentNullException.ThrowIfNull(service);
        return service.EnabledState is "enabled" or "enabled-runtime" or "linked" or "linked-runtime";
    }

    public static bool CanExecute(ServerServiceInfo service, ServerServiceAction action) =>
        action switch
        {
            ServerServiceAction.Start => !service.IsActive,
            ServerServiceAction.Stop => service.IsActive,
            ServerServiceAction.Restart or ServerServiceAction.Reload => service.IsActive,
            ServerServiceAction.Enable => !IsEnabled(service),
            ServerServiceAction.Disable => IsEnabled(service),
            _ => false,
        };

    private static string SearchText(ServerServiceInfo service) =>
        $"{service.Unit} {service.Description} {service.LoadState} {service.ActiveState} {service.SubState} {service.EnabledState} {service.MainProcessId?.ToString() ?? string.Empty} {service.StatusText}";
}
