using ServerDesk.App.Presentation;
using ServerDesk.Application.Docker;
using Xunit;

namespace ServerDesk.Tests;

public sealed class DockerWorkspaceProjectionTests
{
    [Fact]
    public void FilterUsesExistingLocalProjectionAcrossAllResourceKinds()
    {
        var snapshot = Snapshot();

        var worker = DockerWorkspaceProjection.Filter(snapshot, "WORKER");
        var redis = DockerWorkspaceProjection.Filter(snapshot, "redis");
        var blank = DockerWorkspaceProjection.Filter(snapshot, " ");

        Assert.Equal(["worker"], worker.Containers.Select(container => container.Name));
        Assert.Equal(["redis"], redis.Images.Select(image => image.Repository));
        Assert.Equal(snapshot.Containers.Select(container => container.Name), blank.Containers.Select(container => container.Name));
        Assert.Equal(snapshot.Volumes.Select(volume => volume.Name), blank.Volumes.Select(volume => volume.Name));
        Assert.Equal(snapshot.Networks.Select(network => network.Name), blank.Networks.Select(network => network.Name));
    }

    [Fact]
    public void SummarizeUsesSnapshotTotalsAndVisibleCounts()
    {
        var snapshot = Snapshot();
        var visible = DockerWorkspaceProjection.Filter(snapshot, "worker");

        var summary = DockerWorkspaceProjection.Summarize(snapshot, visible);

        Assert.Equal(2, summary.Containers);
        Assert.Equal(1, summary.RunningContainers);
        Assert.Equal(1, summary.Images);
        Assert.Equal(1, summary.Volumes);
        Assert.Equal(1, summary.Networks);
        Assert.Equal(1, summary.VisibleContainers);
        Assert.Equal(0, summary.VisibleImages);
        Assert.Equal(0, summary.VisibleVolumes);
        Assert.Equal(0, summary.VisibleNetworks);
        Assert.Equal(1, visible.ResourceCount);
    }

    [Fact]
    public void SummarizePreservesDockerInfoTotalsWhenInventoryRowsArePartial()
    {
        var snapshot = Snapshot() with
        {
            Containers = [],
            Images = [],
        };
        var visible = DockerWorkspaceProjection.Filter(snapshot, string.Empty);

        var summary = DockerWorkspaceProjection.Summarize(snapshot, visible);

        Assert.Equal(2, summary.Containers);
        Assert.Equal(1, summary.RunningContainers);
        Assert.Equal(1, summary.Images);
        Assert.Equal(0, summary.VisibleContainers);
        Assert.Equal(0, summary.VisibleImages);
    }

    [Fact]
    public void SummarizeFallsBackToContainerStateWhenSystemSummaryIsUnavailable()
    {
        var snapshot = Snapshot() with { System = null };
        var visible = DockerWorkspaceProjection.Filter(snapshot, string.Empty);

        var summary = DockerWorkspaceProjection.Summarize(snapshot, visible);

        Assert.Equal(1, summary.RunningContainers);
        Assert.True(DockerWorkspaceProjection.IsRunning(snapshot.Containers[0]));
        Assert.False(DockerWorkspaceProjection.IsRunning(snapshot.Containers[1]));
    }

    private static DockerInventorySnapshot Snapshot() =>
        new(
            new DockerRuntimeState(
                DockerRuntimeStatus.Available,
                "27.1.1",
                "27.1.1",
                "1.46",
                "Ubuntu",
                "x86_64",
                "Docker runtime available."),
            new DockerSystemSummary(
                2,
                1,
                0,
                1,
                1,
                "overlay2",
                "/var/lib/docker",
                "27.1.1",
                "Ubuntu 24.04",
                "linux",
                "x86_64",
                8,
                16L * 1024 * 1024 * 1024,
                "app-host"),
            [
                new DockerContainerInfo(
                    "sha256:worker",
                    "worker",
                    "example/worker:latest",
                    "running",
                    "Up 10 minutes",
                    "8080/tcp",
                    "/data",
                    "backend",
                    "2026-09-05 10:00:00 +0700",
                    "120MB"),
                new DockerContainerInfo(
                    "sha256:api",
                    "api",
                    "example/api:latest",
                    "exited",
                    "Exited (0)",
                    string.Empty,
                    string.Empty,
                    "backend",
                    "2026-09-05 09:00:00 +0700",
                    "100MB"),
            ],
            [new DockerImageInfo("sha256:redis", "redis", "7", "sha256:digest", "2026-09-01", "45MB")],
            [new DockerVolumeInfo("app-data", "local", "local", "/var/lib/docker/volumes/app-data", "team=backend")],
            [new DockerNetworkInfo("network-id", "backend", "bridge", "local", false, false, "team=backend")]);
}
