using System.Globalization;
using System.Xml.Linq;
using Xunit;

namespace ServerDesk.Tests;

public sealed class OperationHistoryLocalizationTests
{
    [Fact]
    public void EnglishAndVietnameseResourcesHaveIdenticalKeys()
    {
        var english = ReadResources("Strings.OperationHistory.en.xaml");
        var vietnamese = ReadResources("Strings.OperationHistory.vi.xaml");

        Assert.NotEmpty(english);
        Assert.Equal(
            english.Keys.OrderBy(key => key, StringComparer.Ordinal),
            vietnamese.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void BothLanguagesDistinguishAmbiguousStateAndKeepHistoryReadOnly()
    {
        var english = ReadResources("Strings.OperationHistory.en.xaml");
        var vietnamese = ReadResources("Strings.OperationHistory.vi.xaml");

        Assert.Contains("ambiguous", english["Loc.OperationHistory.OutcomeUnknown"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mơ hồ", vietnamese["Loc.OperationHistory.OutcomeUnknown"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot edit", english["Loc.OperationHistory.Footer"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("không thể sửa", vietnamese["Loc.OperationHistory.Footer"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadedStatusFormatsInBothLanguages()
    {
        var english = ReadResources("Strings.OperationHistory.en.xaml");
        var vietnamese = ReadResources("Strings.OperationHistory.vi.xaml");

        var englishText = string.Format(
            CultureInfo.GetCultureInfo("en-US"),
            english["Loc.OperationHistory.LoadedStatus"],
            12,
            250);
        var vietnameseText = string.Format(
            CultureInfo.GetCultureInfo("vi-VN"),
            vietnamese["Loc.OperationHistory.LoadedStatus"],
            12,
            250);

        Assert.Contains("12", englishText, StringComparison.Ordinal);
        Assert.Contains("250", englishText, StringComparison.Ordinal);
        Assert.Contains("12", vietnameseText, StringComparison.Ordinal);
        Assert.Contains("250", vietnameseText, StringComparison.Ordinal);
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
