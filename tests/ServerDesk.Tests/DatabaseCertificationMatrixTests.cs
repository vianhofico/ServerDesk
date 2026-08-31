using ServerDesk.Application.Databases;
using Xunit;

namespace ServerDesk.Tests;

public sealed class DatabaseCertificationMatrixTests
{
    [Theory]
    [InlineData(DatabaseEngineKind.PostgreSql, "18.6")]
    [InlineData(DatabaseEngineKind.MySql, "8.4.11")]
    [InlineData(DatabaseEngineKind.MariaDb, "11.8.9")]
    [InlineData(DatabaseEngineKind.SqlServer, "17.0.4075.5")]
    public void CertifiedSqlFixturesCoverTheFullDatabaseCapabilityPath(DatabaseEngineKind engine, string version)
    {
        Assert.Equal(DatabaseCertificationLevel.Certified, DatabaseCertificationMatrix.LevelFor(engine, version, DatabaseCapabilityKind.RuntimeInventory));
        Assert.Equal(DatabaseCertificationLevel.Certified, DatabaseCertificationMatrix.LevelFor(engine, version, DatabaseCapabilityKind.SshTunneledConnectivity));
        Assert.Equal(DatabaseCertificationLevel.Certified, DatabaseCertificationMatrix.LevelFor(engine, version, DatabaseCapabilityKind.Diagnostics));
        Assert.Equal(DatabaseCertificationLevel.Certified, DatabaseCertificationMatrix.LevelFor(engine, version, DatabaseCapabilityKind.Backup));
        Assert.Equal(DatabaseCertificationLevel.Certified, DatabaseCertificationMatrix.LevelFor(engine, version, DatabaseCapabilityKind.Restore));
    }

    [Fact]
    public void RedisFixtureCertifiesReadOnlyPathButBackupAndRestoreFailClosed()
    {
        Assert.Equal(DatabaseCertificationLevel.Certified, DatabaseCertificationMatrix.LevelFor(DatabaseEngineKind.Redis, "8.10.0", DatabaseCapabilityKind.RuntimeInventory));
        Assert.Equal(DatabaseCertificationLevel.Certified, DatabaseCertificationMatrix.LevelFor(DatabaseEngineKind.Redis, "8.10.0", DatabaseCapabilityKind.SshTunneledConnectivity));
        Assert.Equal(DatabaseCertificationLevel.Certified, DatabaseCertificationMatrix.LevelFor(DatabaseEngineKind.Redis, "8.10.0", DatabaseCapabilityKind.Diagnostics));
        Assert.Equal(DatabaseCertificationLevel.Unsupported, DatabaseCertificationMatrix.LevelFor(DatabaseEngineKind.Redis, "8.10.0", DatabaseCapabilityKind.Backup));
        Assert.Equal(DatabaseCertificationLevel.Unsupported, DatabaseCertificationMatrix.LevelFor(DatabaseEngineKind.Redis, "8.10.0", DatabaseCapabilityKind.Restore));
    }

    [Fact]
    public void UnlistedVersionIsNeverSilentlyPromoted()
    {
        Assert.Equal(
            DatabaseCertificationLevel.Unsupported,
            DatabaseCertificationMatrix.LevelFor(DatabaseEngineKind.PostgreSql, "17.0", DatabaseCapabilityKind.Restore));
        Assert.Equal(
            DatabaseCertificationLevel.Unsupported,
            DatabaseCertificationMatrix.LevelFor(DatabaseEngineKind.SqlServer, "17.0.4075.4", DatabaseCapabilityKind.Backup));
        Assert.Equal(
            DatabaseCertificationLevel.Unsupported,
            DatabaseCertificationMatrix.LevelFor(DatabaseEngineKind.SqlServer, "16.0.1000.6", DatabaseCapabilityKind.Diagnostics));
        Assert.Contains(Enum.GetValues<DatabaseCertificationLevel>(), level => level == DatabaseCertificationLevel.Tested);
    }
}
