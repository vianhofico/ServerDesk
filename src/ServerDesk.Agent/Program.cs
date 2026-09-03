using Microsoft.AspNetCore.Server.Kestrel.Core;
using ServerDesk.Agent;

if (!OperatingSystem.IsLinux())
{
    throw new PlatformNotSupportedException("serverdesk-agent is supported on Linux only.");
}

var builder = WebApplication.CreateBuilder(args);
var listener = AgentListenerOptions.FromConfiguration(builder.Configuration);
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Listen(listener.CreateEndpoint(), listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http2;
    });
});

builder.Services.AddGrpc(options =>
{
    options.EnableDetailedErrors = false;
    options.MaxReceiveMessageSize = 64 * 1024;
    options.MaxSendMessageSize = 64 * 1024;
});
builder.Services.AddSingleton(AgentRuntimeInfo.Create());
builder.Services.AddSingleton<IAgentMetricsSampler, LinuxMetricsSampler>();
builder.Services.AddSingleton<IAgentProcessSnapshotReader, LinuxProcessSnapshotReader>();
builder.Services.AddSingleton<IAgentServiceSnapshotReader, SystemdServiceSnapshotReader>();
builder.Services.AddSingleton<IAgentDockerEventReader, DockerCliEventReader>();

var app = builder.Build();
app.MapGrpcService<AgentControlService>();
await app.RunAsync();
