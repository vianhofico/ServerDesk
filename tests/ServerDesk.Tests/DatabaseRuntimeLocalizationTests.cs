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
    public void BothLanguagesDescribeReadOnlyRuntimeDiscoveryAndSqlServerBoundary()
    {
        var english = ReadResources("Strings.DatabaseRuntime.en.xaml");
        var vietnamese = ReadResources("Strings.DatabaseRuntime.vi.xaml");

        Assert.Contains("read-only", english["Loc.DatabaseRuntime.Subtitle"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chỉ đọc", vietnamese["Loc.DatabaseRuntime.Subtitle"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Microsoft SQL Server", english["Loc.DatabaseRuntime.Subtitle"], StringComparison.Ordinal);
        Assert.Contains("Microsoft SQL Server", vietnamese["Loc.DatabaseRuntime.Subtitle"], StringComparison.Ordinal);
        Assert.Contains("client tooling", english["Loc.DatabaseRuntime.Subtitle"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("client tool", vietnamese["Loc.DatabaseRuntime.Subtitle"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not request database passwords", english["Loc.DatabaseRuntime.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("không yêu cầu mật khẩu DB", vietnamese["Loc.DatabaseRuntime.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not execute arbitrary SQL", english["Loc.DatabaseRuntime.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("không chạy SQL tùy ý", vietnamese["Loc.DatabaseRuntime.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BothLanguagesStateSecretTunnelAndReadOnlyDiagnosticBoundaries()
    {
        var english = ReadResources("Strings.DatabaseRuntime.en.xaml");
        var vietnamese = ReadResources("Strings.DatabaseRuntime.vi.xaml");

        Assert.Contains("secure secret store", english["Loc.DatabaseProfiles.Subtitle"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kho secret", vietnamese["Loc.DatabaseProfiles.Subtitle"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Microsoft SQL Server", english["Loc.DatabaseProfiles.Subtitle"], StringComparison.Ordinal);
        Assert.Contains("Microsoft SQL Server", vietnamese["Loc.DatabaseProfiles.Subtitle"], StringComparison.Ordinal);
        Assert.Contains("127.0.0.1", english["Loc.DatabaseProfiles.SafetyFooter"], StringComparison.Ordinal);
        Assert.Contains("127.0.0.1", vietnamese["Loc.DatabaseProfiles.SafetyFooter"], StringComparison.Ordinal);
        Assert.Contains("no silent direct/public fallback", english["Loc.DatabaseProfiles.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("không có fallback âm thầm", vietnamese["Loc.DatabaseProfiles.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TDS pre-login", english["Loc.DatabaseProfiles.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TDS pre-login", vietnamese["Loc.DatabaseProfiles.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no arbitrary query console", english["Loc.DatabaseProfiles.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("không có console truy vấn tùy ý", vietnamese["Loc.DatabaseProfiles.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("credentials are never displayed", english["Loc.DatabaseProfiles.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("không bao giờ hiển thị thông tin xác thực", vietnamese["Loc.DatabaseProfiles.SafetyFooter"], StringComparison.OrdinalIgnoreCase);
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
