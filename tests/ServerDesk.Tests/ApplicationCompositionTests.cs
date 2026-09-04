using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ServerDesk.Application.Agent;
using ServerDesk.Infrastructure.Ssh.Agent;
using Xunit;

namespace ServerDesk.Tests;

public sealed class ApplicationCompositionTests
{
    [Fact]
    public void StartupServiceGraphBuildsWithValidation()
    {
        var services = new ServiceCollection();
        var configureServices = typeof(ServerDesk.App.App).GetMethod(
            "ConfigureServices",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(configureServices);
        configureServices.Invoke(null, new object[] { services });

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

        var tunnelFactory = provider.GetRequiredService<IAgentTunnelSessionFactory>();
        Assert.IsType<SshAgentTunnelSessionFactory>(tunnelFactory);
    }
}
