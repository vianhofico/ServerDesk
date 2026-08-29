using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace ServerDesk.Tests;

public sealed partial class DatabaseRestoreLocalizationTests
{
    [Fact]
    public void EnglishAndVietnameseResourcesHaveIdenticalKeysAndFormatPlaceholders()
    {
        var english = ReadResources("Strings.DatabaseRestores.en.xaml");
        var vietnamese = ReadResources("Strings.DatabaseRestores.vi.xaml");

        Assert.NotEmpty(english);
        Assert.Equal(
            english.Keys.OrderBy(key => key, StringComparer.Ordinal),
            vietnamese.Keys.OrderBy(key => key, StringComparer.Ordinal));

        foreach (var key in english.Keys)
        {
            Assert.Equal(Placeholders(english[key]), Placeholders(vietnamese[key]));
        }
    }

    [Fact]
    public void BothLanguagesStateDestructiveRestoreSafetyBoundaries()
    {
        var english = ReadResources("Strings.DatabaseRestores.en.xaml");
        var vietnamese = ReadResources("Strings.DatabaseRestores.vi.xaml");

        Assert.Contains("only a selected verified manifest", english["Loc.DatabaseRestores.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chỉ manifest đã xác minh", vietnamese["Loc.DatabaseRestores.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sensitive stdin", english["Loc.DatabaseRestores.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sensitive stdin", vietnamese["Loc.DatabaseRestores.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never blindly retried", english["Loc.DatabaseRestores.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("không bao giờ bị retry mù", vietnamese["Loc.DatabaseRestores.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Redis fails closed", english["Loc.DatabaseRestores.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Redis fail-closed", vietnamese["Loc.DatabaseRestores.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No automatic rollback", english["Loc.DatabaseRestores.Confirm"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Không có rollback tự động", vietnamese["Loc.DatabaseRestores.Confirm"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("destructive", english["Loc.DatabaseRestores.Confirm"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("destructive", vietnamese["Loc.DatabaseRestores.Confirm"], StringComparison.OrdinalIgnoreCase);
    }

    private static string[] Placeholders(string value) => PlaceholderRegex()
        .Matches(value)
        .Select(match => match.Value)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(item => item, StringComparer.Ordinal)
        .ToArray();

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

    [GeneratedRegex("\\{\\d+\\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();
}
