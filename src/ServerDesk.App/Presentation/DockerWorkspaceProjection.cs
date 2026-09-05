using ServerDesk.Application.Docker;

namespace ServerDesk.App.Presentation;

public sealed record DockerVisibleInventory(
    IReadOnlyList<DockerContainerInfo> Containers,
    IReadOnlyList<DockerImageInfo> Images,
    IReadOnlyList<DockerVolumeInfo> Volumes,
    IReadOnlyList<DockerNetworkInfo> Networks)
{
    public int ResourceCount => Containers.Count + Images.Count + Volumes.Count + Networks.Count;
}

public sealed record DockerWorkspaceSummary(
    int Containers,
    int RunningContainers,
    int Images,
    int Volumes,
    int Networks,
    int VisibleContainers,
    int VisibleImages,
    int VisibleVolumes,
    int VisibleNetworks);

public static class DockerWorkspaceProjection
{
    public static DockerVisibleInventory Filter(DockerInventorySnapshot snapshot, string? search)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var query = search ?? string.Empty;
        return new DockerVisibleInventory(
            DockerInventoryProjection.FilterContainers(snapshot.Containers, query),
            DockerInventoryProjection.FilterImages(snapshot.Images, query),
            DockerInventoryProjection.FilterVolumes(snapshot.Volumes, query),
            DockerInventoryProjection.FilterNetworks(snapshot.Networks, query));
    }

    public static DockerWorkspaceSummary Summarize(
        DockerInventorySnapshot snapshot,
        DockerVisibleInventory visible)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(visible);

        var running = snapshot.System?.ContainersRunning ??
                      snapshot.Containers.Count(container => IsRunning(container));
        return new DockerWorkspaceSummary(
            snapshot.Containers.Count,
            Math.Clamp(running, 0, snapshot.Containers.Count),
            snapshot.Images.Count,
            snapshot.Volumes.Count,
            snapshot.Networks.Count,
            visible.Containers.Count,
            visible.Images.Count,
            visible.Volumes.Count,
            visible.Networks.Count);
    }

    public static bool IsRunning(DockerContainerInfo container)
    {
        ArgumentNullException.ThrowIfNull(container);
        return string.Equals(container.State, "running", StringComparison.OrdinalIgnoreCase);
    }
}
