using System.Xml.Linq;
using Xunit;

namespace ServerDesk.Tests;

public sealed class BackupRestoreLocalizationTests
{
    [Fact]
    public void EnglishAndVietnameseResourcesHaveIdenticalKeys()
    {
        var english = ReadResources("Strings.BackupRestore.en.xaml");
        var vietnamese = ReadResources("Strings.BackupRestore.vi.xaml");
        Assert.NotEmpty(english);
        Assert.Equal(
            english.Keys.OrderBy(key => key, StringComparer.Ordinal),
            vietnamese.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void BothLanguagesStateExactTargetAndNoBlindRetryPolicy()
    {
        var english = ReadResources("Strings.BackupRestore.en.xaml");
        var vietnamese = ReadResources("Strings.BackupRestore.vi.xaml");

        Assert.Contains(english.Values, value =>
            value.Contains("exact original", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(english.Values, value =>
            value.Contains("never blindly retried", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(vietnamese.Values, value =>
            value.Contains("target gốc", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("target", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(vietnamese.Values, value =>
            value.Contains("không bao giờ", StringComparison.OrdinalIgnoreCase) &&
            value.Contains("retry", StringComparison.OrdinalIgnoreCase));
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
