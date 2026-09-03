using System.Globalization;
using System.Net;
using Microsoft.Extensions.Configuration;

namespace ServerDesk.Agent;

public sealed class AgentListenerOptions
{
    public const int DefaultPort = 41371;
    public const string PortConfigurationKey = "Agent:Port";
    public const string PortEnvironmentVariable = "SERVERDESK_AGENT_PORT";

    private AgentListenerOptions(int port)
    {
        Port = port;
    }

    public int Port { get; }

    public static AgentListenerOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var rawPort = configuration[PortConfigurationKey];
        if (string.IsNullOrWhiteSpace(rawPort))
        {
            rawPort = Environment.GetEnvironmentVariable(PortEnvironmentVariable);
        }

        if (string.IsNullOrWhiteSpace(rawPort))
        {
            return FromPort(DefaultPort);
        }

        if (!int.TryParse(rawPort, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            throw new InvalidOperationException("The configured agent port is invalid.");
        }

        return FromPort(port);
    }

    public static AgentListenerOptions FromPort(int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Agent port must be between 1 and 65535.");
        }

        return new AgentListenerOptions(port);
    }

    public IPEndPoint CreateEndpoint() => new(IPAddress.Loopback, Port);
}
