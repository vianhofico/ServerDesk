using System.Xml.Linq;
using Xunit;

namespace ServerDesk.Tests;

public sealed class DatabaseRuntimeLocalizationTests
{
    [Fact]
    public void EnglishAndVietnameseResourcesHaveIdenticalKeys()
    {
        var english = ReadResources("Strings.DatabaseRuntime.en.xaml");
        var vietnamese = ReadResources("Strings.DatabaseRuntime.vi.xaml");

        Assert.NotEmpty(english);
        Assert.Equal(
            english.Keys.OrderBy(key => key, StringComparer.Ordinal),
            vietnamese.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void BothLanguagesStateThatDiscoveryDoesNotConnectToDatabases()
    {
        var english = ReadResources("Strings.DatabaseRuntime.en.xaml");
        var vietnamese = ReadResources("Strings.DatabaseRuntime.vi.xaml");

        Assert.Contains("does not connect", english["Loc.DatabaseRuntime.Subtitle"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("không kết nối", vietnamese["Loc.DatabaseRuntime.Subtitle"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no DB login", english["Loc.DatabaseRuntime.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("không đăng nhập DB", vietnamese["Loc.DatabaseRuntime.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BothLanguagesStateSecretTunnelAndReadOnlyDiagnosticBoundaries()
    {
        var english = ReadResources("Strings.DatabaseRuntime.en.xaml");
        var vietnamese = ReadResources("Strings.DatabaseRuntime.vi.xaml");

        Assert.Contains("secure secret store", english["Loc.DatabaseProfiles.Subtitle"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kho secret", vietnamese["Loc.DatabaseProfiles.Subtitle"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("127.0.0.1", english["Loc.DatabaseProfiles.SafetyFooter"], StringComparison.Ordinal);
        Assert.Contains("127.0.0.1", vietnamese["Loc.DatabaseProfiles.SafetyFooter"], StringComparison.Ordinal);
        Assert.Contains("no silent direct", english["Loc.DatabaseProfiles.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("không có cơ chế âm thầm", vietnamese["Loc.DatabaseProfiles.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("protocol probe", english["Loc.DatabaseProfiles.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("probe giao thức", vietnamese["Loc.DatabaseProfiles.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no arbitrary query console", english["Loc.DatabaseProfiles.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("không có console truy vấn tùy ý", vietnamese["Loc.DatabaseProfiles.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no mutation", english["Loc.DatabaseProfiles.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("không mutation", vietnamese["Loc.DatabaseProfiles.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never displayed", english["Loc.DatabaseProfiles.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("không bao giờ hiển thị", vietnamese["Loc.DatabaseProfiles.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("credentials were not tested", english["Loc.DatabaseProfiles.TunnelSucceeded"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chưa kiểm tra thông tin đăng nhập", vietnamese["Loc.DatabaseProfiles.TunnelSucceeded"], StringComparison.OrdinalIgnoreCase);
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
