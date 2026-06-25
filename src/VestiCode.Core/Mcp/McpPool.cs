using Microsoft.Extensions.Logging;

namespace VestiCode.Core.Mcp;

/// <summary>MCP 连接池：启动时并行连接所有配置的 server，失败不阻塞其余。</summary>
public sealed class McpPool(IReadOnlyList<McpServerConfig> configs, ILogger logger)
{
    private readonly Dictionary<string, McpClient> _clients = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> ConnectedServers => _clients.Keys;

    public async Task<IReadOnlyDictionary<string, McpClient>> ConnectAllAsync(CancellationToken ct = default)
    {
        var tasks = configs.Select(async config =>
        {
            try
            {
                var client = new McpClient(CreateTransport(config), config.Name);
                await client.ConnectAsync(ct).ConfigureAwait(false);
                return (config.Name, client: (McpClient?)client);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "MCP [{Server}] 连接失败", config.Name);
                return (config.Name, client: null);
            }
        });

        foreach (var (name, client) in await Task.WhenAll(tasks).ConfigureAwait(false))
        {
            if (client is not null)
            {
                _clients[name] = client;
            }
        }
        return _clients;
    }

    public async Task ShutdownAsync()
    {
        foreach (var client in _clients.Values)
        {
            await client.DisconnectAsync().ConfigureAwait(false);
        }
        _clients.Clear();
    }

    private static IMcpTransport CreateTransport(McpServerConfig config) => config.Transport switch
    {
        "stdio" => new StdioTransport(config.Command, config.Args, config.Env, config.Timeout),
        "http" => new HttpTransport(config.Url, config.Headers, config.Timeout),
        _ => throw new NotSupportedException($"不支持的传输方式: {config.Transport}"),
    };
}
