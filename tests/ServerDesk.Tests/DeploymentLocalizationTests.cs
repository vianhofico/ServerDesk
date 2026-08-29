using System.Globalization;
using System.Xml.Linq;
using Xunit;

namespace ServerDesk.Tests;

public sealed class DeploymentLocalizationTests
{
    [Fact]
    public void DeploymentEnglishAndVietnameseResourcesHaveIdenticalKeys()
    {
        var english = ReadResources("Strings.Deploy.en.xaml");
        var vietnamese = ReadResources("Strings.Deploy.vi.xaml");

        Assert.NotEmpty(english);
        Assert.Equal(
            english.Keys.OrderBy(key => key, StringComparer.Ordinal),
            vietnamese.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("Loc.Deploy.ExecuteConfirmMessage", "api-prod", "prod", 4)]
    [InlineData("Loc.Deploy.RollbackConfirmMessage", "api-prod", "prod", 0)]
    public void DeploymentParameterizedResourcesFormatInBothLanguages(
        string key,
        string targetId,
        string environment,
        int mutationCount)
    {
        var english = ReadResources("Strings.Deploy.en.xaml");
        var vietnamese = ReadResources("Strings.Deploy.vi.xaml");
        var arguments = key == "Loc.Deploy.ExecuteConfirmMessage"
            ? new object?[] { targetId, environment, mutationCount }
            : new object?[] { targetId, environment };

        var englishText = string.Format(CultureInfo.GetCultureInfo("en-US"), english[key], arguments);
        var vietnameseText = string.Format(CultureInfo.GetCultureInfo("vi-VN"), vietnamese[key], arguments);

        Assert.Contains(targetId, englishText, StringComparison.Ordinal);
        Assert.Contains(environment, englishText, StringComparison.Ordinal);
        Assert.Contains(targetId, vietnameseText, StringComparison.Ordinal);
        Assert.Contains(environment, vietnameseText, StringComparison.Ordinal);
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
