using ServerDesk.Application.EnvironmentFiles;
using Xunit;

namespace ServerDesk.Tests;

public sealed class EnvironmentFileFixtureTests
{
    [Theory]
    [InlineData("ubuntu-24.04", "APP_URL", "https://app24.example.test", true)]
    [InlineData("ubuntu-26.04", "PUBLIC_URL", "https://edge26.example.test", false)]
    [InlineData("debian-13", "APP_URL", "https://legacy13.example.test", true)]
    public async Task CertifiedDistroFixturesPreserveSupportedAndUnsupportedSyntax(
        string distro,
        string key,
        string expectedValue,
        bool expectedUnsupported)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "EnvironmentFiles", distro + ".txt");
        var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

        var parsed = EnvironmentFileParser.Parse(text);
        var entry = Assert.Single(parsed.Entries, candidate => candidate.Key == key);

        Assert.Equal(expectedValue, entry.Value);
        Assert.Equal(expectedUnsupported, parsed.HasUnsupportedLines);
        Assert.Equal(text, EnvironmentFileTextForTest.RoundTrip(text));
    }

    [Theory]
    [InlineData("ubuntu-24.04", "DATABASE_PASSWORD")]
    [InlineData("ubuntu-26.04", "PUBLIC_VALUE")]
    [InlineData("debian-13", "CONNECTION")]
    public async Task CertifiedDistroFixturesMaskSecretLookingEntries(string distro, string key)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "EnvironmentFiles", distro + ".txt");
        var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        var parsed = EnvironmentFileParser.Parse(text);
        var entry = Assert.Single(parsed.Entries, candidate => candidate.Key == key);

        Assert.True(entry.IsSecret);
        Assert.Equal(EnvironmentSecretClassifier.Mask, EnvironmentSecretClassifier.DisplayValue(entry, revealed: false));
    }

    private static class EnvironmentFileTextForTest
    {
        public static string RoundTrip(string text)
        {
            var parsed = EnvironmentFileParser.Parse(text);
            var result = text;
            foreach (var entry in parsed.Entries)
            {
                result = EnvironmentFileEditor.SetValueAtLine(result, entry.LineNumber, entry.Key, entry.Value);
            }

            return result;
        }
    }
}
