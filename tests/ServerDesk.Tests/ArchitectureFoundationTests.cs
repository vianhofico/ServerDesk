using ServerDesk.Application.Profiles;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class ArchitectureFoundationTests
{
    [Fact]
    public void DomainHasNoUiPersistenceOrPlatformDependencies()
    {
        var references = typeof(ServerProfile).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(references, name => name.StartsWith("Presentation", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("WindowsBase", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("ServerDesk.Platform", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("ServerDesk.Infrastructure", StringComparison.Ordinal));
    }

    [Fact]
    public void ApplicationDoesNotDependOnConcreteInfrastructure()
    {
        var references = typeof(IProfileRepository).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("ServerDesk.Platform", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("ServerDesk.Infrastructure", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("PresentationFramework", StringComparison.Ordinal));
    }
}
