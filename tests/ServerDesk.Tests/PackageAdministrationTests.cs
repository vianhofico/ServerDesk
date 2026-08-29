using ServerDesk.Application.Packages;
using ServerDesk.Domain.Operations;
using Xunit;

namespace ServerDesk.Tests;

public sealed class PackageAdministrationTests
{
    [Fact]
    public void NormalizeRejectsShellLikeOrOptionPackageIdentities()
    {
        Assert.Throws<ArgumentException>(() => PackageAdministrationService.NormalizeRequest(
            new PackageMutationRequest(PackageMutationKind.Install, PackageManagerKind.Apt, ["--allowerasing"])));
        Assert.Throws<ArgumentException>(() => PackageAdministrationService.NormalizeRequest(
            new PackageMutationRequest(PackageMutationKind.Install, PackageManagerKind.Apt, ["nginx;rm"])));
        Assert.Throws<ArgumentException>(() => PackageAdministrationService.NormalizeRequest(
            new PackageMutationRequest(PackageMutationKind.Install, PackageManagerKind.Apt, [])));
    }

    [Fact]
    public void NormalizeRefreshCarriesNoPackageIdentity()
    {
        var request = PackageAdministrationService.NormalizeRequest(
            new PackageMutationRequest(PackageMutationKind.RefreshMetadata, PackageManagerKind.Apt, []));

        Assert.Empty(request.PackageNames);
    }

    [Fact]
    public void AptCommandsAreSelectedPackageOnlyAndNeverGlobalUpgrade()
    {
        var refresh = PackageAdministrationService.BuildCommandForTest(
            new PackageMutationRequest(PackageMutationKind.RefreshMetadata, PackageManagerKind.Apt, []));
        var upgrade = PackageAdministrationService.BuildCommandForTest(
            new PackageMutationRequest(PackageMutationKind.Upgrade, PackageManagerKind.Apt, ["nginx", "openssl"]));

        Assert.Equal("sudo", refresh.Executable);
        Assert.Equal(["-n", "apt-get", "update"], refresh.Arguments);
        Assert.Equal(
            ["-n", "apt-get", "-y", "--only-upgrade", "install", "nginx", "openssl"],
            upgrade.Arguments);
        Assert.Equal(OperationRisk.Mutating, upgrade.Risk);
        Assert.DoesNotContain("dist-upgrade", upgrade.Arguments);
        Assert.DoesNotContain("full-upgrade", upgrade.Arguments);
    }

    [Fact]
    public void DnfCommandsAreSelectedPackageOnlyAndRemoveIsDestructive()
    {
        var upgrade = PackageAdministrationService.BuildCommandForTest(
            new PackageMutationRequest(PackageMutationKind.Upgrade, PackageManagerKind.Dnf, ["openssl"]));
        var remove = PackageAdministrationService.BuildCommandForTest(
            new PackageMutationRequest(PackageMutationKind.Remove, PackageManagerKind.Dnf, ["httpd"]));

        Assert.Equal(["-n", "dnf", "-q", "-y", "upgrade", "openssl"], upgrade.Arguments);
        Assert.Equal(["-n", "dnf", "-q", "-y", "remove", "httpd"], remove.Arguments);
        Assert.Equal(OperationRisk.Destructive, remove.Risk);
        Assert.DoesNotContain("upgrade-minimal", upgrade.Arguments);
    }

    [Fact]
    public void DebianUbuntuFixtureParsesInstalledAndSecurityUpdateMetadata()
    {
        var installed = PackageAdministrationService.ParseDpkgInstalled(
            "nginx\t1.24.0-2ubuntu7\tamd64\nopenssl\t3.0.13-0ubuntu3\tamd64\n");
        var updates = PackageAdministrationService.ParseAptUpgradeSimulation(
            "Inst openssl [3.0.13-0ubuntu3] (3.0.13-0ubuntu3.1 Ubuntu:24.04/noble-security [amd64])\n" +
            "Inst nginx [1.24.0-2ubuntu7] (1.24.0-2ubuntu7.1 Ubuntu:24.04/noble-updates [amd64])\n");

        Assert.Equal(2, installed.Count);
        var security = Assert.Single(updates, item => item.Name == "openssl");
        Assert.Equal("3.0.13-0ubuntu3.1", security.CandidateVersion);
        Assert.Equal(PackageUpdateClassification.Security, security.Classification);
        Assert.Equal(PackageUpdateClassification.Regular, Assert.Single(updates, item => item.Name == "nginx").Classification);
    }

    [Fact]
    public void RhelFamilyFixtureParsesInstalledAndCachedUpdates()
    {
        var installed = PackageAdministrationService.ParseRpmInstalled(
            "httpd\t2.4.62-1.el9\tx86_64\nopenssl-libs\t3.2.2-6.el9\tx86_64\n");
        var updates = PackageAdministrationService.ParseDnfCheckUpdate(
            "Last metadata expiration check: 0:10:00 ago.\n" +
            "openssl-libs.x86_64 3.2.2-7.el9 rhel-9-baseos-security\n" +
            "httpd.x86_64 2.4.62-2.el9 rhel-9-appstream\n");

        Assert.Equal(2, installed.Count);
        Assert.Equal(PackageUpdateClassification.Security, Assert.Single(updates, item => item.Name == "openssl-libs").Classification);
        Assert.Equal(PackageUpdateClassification.Regular, Assert.Single(updates, item => item.Name == "httpd").Classification);
    }
}
