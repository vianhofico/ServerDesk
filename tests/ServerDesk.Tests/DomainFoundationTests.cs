using System.Text.Json;
using ServerDesk.Application.Settings;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class DomainFoundationTests
{
    [Fact]
    public void ServerProfileCreateNormalizesInput()
    {
        var reference = SecretReference.Create("ssh-password");

        var profile = ServerProfile.Create(
            "  Production API  ",
            "  prod.example.com  ",
            22,
            "  deploy  ",
            "  Production  ",
            reference);

        Assert.Equal("Production API", profile.Name);
        Assert.Equal("prod.example.com", profile.Host);
        Assert.Equal("deploy", profile.Username);
        Assert.Equal("Production", profile.Environment);
        Assert.Equal(reference, profile.CredentialReference);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(-1)]
    public void ServerProfileRejectsInvalidPort(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ServerProfile.Create("Server", "localhost", port, "user"));
    }

    [Fact]
    public void SecretReferenceIsOpaqueWhenFormatted()
    {
        var reference = SecretReference.Create("ssh-key-passphrase");

        Assert.StartsWith("serverdesk:ssh-key-passphrase:", reference.Value, StringComparison.Ordinal);
        Assert.Equal("[secret-reference]", reference.ToString());
    }

    [Fact]
    public void ProfileSerializationCannotContainASecretThatWasNeverStoredInTheProfile()
    {
        const string rawSecret = "do-not-persist-this-secret";
        var reference = SecretReference.Create("ssh-password");
        var profile = ServerProfile.Create("Server", "localhost", 22, "user", credentialReference: reference);

        var json = JsonSerializer.Serialize(profile);

        Assert.DoesNotContain(rawSecret, json, StringComparison.Ordinal);
        Assert.Contains(reference.Value, json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AppThemePreference.Light, AppThemeKind.Dark, AppThemeKind.Light)]
    [InlineData(AppThemePreference.Dark, AppThemeKind.Light, AppThemeKind.Dark)]
    [InlineData(AppThemePreference.System, AppThemeKind.Light, AppThemeKind.Light)]
    [InlineData(AppThemePreference.System, AppThemeKind.Dark, AppThemeKind.Dark)]
    public void ThemePreferenceResolvesPredictably(
        AppThemePreference preference,
        AppThemeKind systemTheme,
        AppThemeKind expected)
    {
        Assert.Equal(expected, ThemePreferenceResolver.Resolve(preference, systemTheme));
    }
}
