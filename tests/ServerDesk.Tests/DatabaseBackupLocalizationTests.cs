using System.Xml.Linq;
using Xunit;

namespace ServerDesk.Tests;

public sealed class DatabaseBackupLocalizationTests
{
    [Fact]
    public void EnglishAndVietnameseResourcesHaveIdenticalKeys()
    {
        var english = ReadResources("Strings.DatabaseBackups.en.xaml");
        var vietnamese = ReadResources("Strings.DatabaseBackups.vi.xaml");

        Assert.NotEmpty(english);
        Assert.Equal(
            english.Keys.OrderBy(key => key, StringComparer.Ordinal),
            vietnamese.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void BothLanguagesStateVerificationSecretAndAmbiguousCompletionBoundaries()
    {
        var english = ReadResources("Strings.DatabaseBackups.en.xaml");
        var vietnamese = ReadResources("Strings.DatabaseBackups.vi.xaml");

        Assert.Contains("only verified", english["Loc.DatabaseBackups.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chỉ artifact đã xác minh", vietnamese["Loc.DatabaseBackups.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sensitive stdin", english["Loc.DatabaseBackups.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sensitive stdin", vietnamese["Loc.DatabaseBackups.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never blindly retried", english["Loc.DatabaseBackups.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("không bao giờ bị retry mù", vietnamese["Loc.DatabaseBackups.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Redis fails closed", english["Loc.DatabaseBackups.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Redis fail-closed", vietnamese["Loc.DatabaseBackups.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string> ReadResources(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Localization", fileName);
        var document = XDocument.Load(path, LoadOptions.None);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return document.Root!
            .Elements()
            .Where(element => element.Attribute(x + "Key") is not null)
            .ToDictionary(
                element => element.Attribute(x + "Key")!.Value,
                element => element.Value,
                StringComparer.Ordinal);
    }
}
