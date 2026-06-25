using Microsoft.Extensions.Logging;
using VestiCode.Core.Tools;

namespace VestiCode.Core.Mcp;

/// <summary>
/// MCP 管理器：启动时连接所有 server，急切发现 tools（注册进工具表供 LLM 可见），
/// resources/prompts 适配器以延迟方式注册（首次执行才发现具体项）。
/// </summary>
public sealed class McpManager(ToolRegistry toolRegistry, ILogger<McpManager> logger)
{
    private McpPool? _pool;
    private IReadOnlyList<McpServerConfig> _configs = [];

    public bool IsConfigured => _configs.Count > 0;

    public IReadOnlyCollection<string> ConnectedServers => _pool?.ConnectedServers ?? [];

    /// <summary>加载配置（项目 + 全局）。</summary>
    public IReadOnlyList<McpServerConfig> LoadConfig()
    {
        _configs = McpConfigLoader.Load();
        _pool = new McpPool(_configs, logger);
        return _configs;
    }

    /// <summary>连接所有 server 并把适配器注册进工具表，返回注册的适配器数。</summary>
    public async Task<int> DiscoverAndRegisterAsync(CancellationToken ct = default)
    {
        if (_pool is null)
        {
            return 0;
        }

        var clients = await _pool.ConnectAllAsync(ct).ConfigureAwait(false);
        var count = 0;
        foreach (var (name, client) in clients)
        {
            try
            {
                foreach (var toolDef in await client.ListToolsAsync(ct).ConfigureAwait(false))
                {
                    toolRegistry.Register(new McpToolAdapter(client, toolDef));
                    count++;
                }
                toolRegistry.Register(new McpResourceAdapter(client));
                toolRegistry.Register(new McpPromptAdapter(client));
                count += 2;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "MCP [{Server}] 发现失败", name);
            }
        }
        return count;
    }

    public async Task ShutdownAsync()
    {
        if (_pool is not null)
        {
            await _pool.ShutdownAsync().ConfigureAwait(false);
        }
    }
}
