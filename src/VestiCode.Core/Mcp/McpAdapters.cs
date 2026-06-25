using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VestiCode.Core.Tools;

namespace VestiCode.Core.Mcp;

/// <summary>
/// MCP 工具命名：<c>{server}__{tool}</c>，并清洗为 LLM 函数名允许的 <c>[a-zA-Z0-9_-]</c>
/// （OpenAI/Anthropic/DeepSeek 都不允许 <c>/</c>，故不能用 server/tool）。
/// </summary>
public static partial class McpNaming
{
    [GeneratedRegex("[^a-zA-Z0-9_-]")]
    private static partial Regex Invalid();

    public static string Safe(string server, string tool) => Invalid().Replace($"{server}__{tool}", "_");
}

/// <summary>把单个 MCP 工具包装为 VestiCode 的 ITool（命名 <c>{server}__{tool}</c> 防冲突）。</summary>
public sealed class McpToolAdapter(McpClient client, JsonObject toolDef) : ITool
{
    private readonly string _toolName = toolDef["name"]?.GetValue<string>() ?? "";

    public string Name => McpNaming.Safe(client.ServerName, _toolName);

    public string Description => toolDef["description"]?.GetValue<string>() ?? "";

    /// <summary>
    /// 按工具名启发式推断读/写：含 read/list/get/search/find/tree/info/stat/grep/glob 等词视为只读，
    /// 否则保守视为写（影响读并发/写串行，以及 Normal 档是否需要 HITL 确认）。
    /// </summary>
    public ToolCategory Category => IsReadOnlyName(_toolName) ? ToolCategory.Read : ToolCategory.Write;

    private static bool IsReadOnlyName(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("read") || n.Contains("list") || n.Contains("get")
            || n.Contains("search") || n.Contains("find") || n.Contains("tree")
            || n.Contains("info") || n.Contains("stat") || n.Contains("grep")
            || n.Contains("glob") || n.Contains("query") || n.Contains("show") || n.Contains("cat");
    }

    public IReadOnlyList<ToolParameter> Parameters
    {
        get
        {
            var schema = toolDef["inputSchema"] as JsonObject;
            var props = schema?["properties"] as JsonObject;
            var required = (schema?["required"] as JsonArray)?.Select(n => n?.GetValue<string>()).ToHashSet() ?? [];
            var list = new List<ToolParameter>();
            if (props is not null)
            {
                foreach (var (name, node) in props)
                {
                    var po = node as JsonObject;
                    list.Add(new ToolParameter(
                        name,
                        po?["type"]?.GetValue<string>() ?? "string",
                        po?["description"]?.GetValue<string>() ?? "",
                        required.Contains(name)));
                }
            }
            return list;
        }
    }

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            return ToolResult.Ok(await client.CallToolAsync(_toolName, arguments, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex.Message);
        }
    }
}

/// <summary>延迟资源适配器：不带 uri 调用时首次惰性 list_resources 列举（缓存），带 uri 则读取。</summary>
public sealed class McpResourceAdapter(McpClient client) : ITool
{
    private IReadOnlyList<JsonObject>? _catalog; // 首次发现后缓存

    public string Name => McpNaming.Safe(client.ServerName, "mcp_resource");
    public string Description => $"列举或读取 MCP server '{client.ServerName}' 上的资源。省略 uri 时返回可用资源清单（首次调用惰性发现），传 uri 时读取该资源。";
    public ToolCategory Category => ToolCategory.Read;
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("uri", "string", "资源 URI，如 file:///path/to/file；省略则列出可用资源", Required: false),
    ];

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            var uri = arguments.GetStringOrDefault("uri");
            if (string.IsNullOrWhiteSpace(uri))
            {
                _catalog ??= await client.ListResourcesAsync(cancellationToken).ConfigureAwait(false);
                if (_catalog.Count == 0)
                {
                    return ToolResult.Ok("(该 server 暂无可用资源)");
                }
                var list = string.Join("\n", _catalog.Select(r =>
                    $"- {r["uri"]?.GetValue<string>() ?? "?"}  {r["name"]?.GetValue<string>() ?? ""}"));
                return ToolResult.Ok($"可用资源：\n{list}\n\n再次调用并传入 uri 参数以读取具体资源。");
            }
            return ToolResult.Ok(await client.ReadResourceAsync(uri, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex.Message);
        }
    }
}

/// <summary>延迟提示词适配器：不带 name 调用时首次惰性 list_prompts 列举（缓存），带 name 则获取模板。</summary>
public sealed class McpPromptAdapter(McpClient client) : ITool
{
    private IReadOnlyList<JsonObject>? _catalog; // 首次发现后缓存

    public string Name => McpNaming.Safe(client.ServerName, "mcp_prompt");
    public string Description => $"列举或获取 MCP server '{client.ServerName}' 上的提示词模板。省略 name 时返回可用模板清单（首次调用惰性发现），传 name 时获取该模板。";
    public ToolCategory Category => ToolCategory.Read;
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("name", "string", "提示词模板名称；省略则列出可用模板", Required: false),
        new("arguments", "string", "模板参数，JSON 格式", Required: false),
    ];

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, CancellationToken cancellationToken = default)
    {
        var name = arguments.GetStringOrDefault("name");
        if (string.IsNullOrWhiteSpace(name))
        {
            try
            {
                _catalog ??= await client.ListPromptsAsync(cancellationToken).ConfigureAwait(false);
                if (_catalog.Count == 0)
                {
                    return ToolResult.Ok("(该 server 暂无可用提示词模板)");
                }
                var list = string.Join("\n", _catalog.Select(p =>
                    $"- {p["name"]?.GetValue<string>() ?? "?"}  {p["description"]?.GetValue<string>() ?? ""}"));
                return ToolResult.Ok($"可用提示词模板：\n{list}\n\n再次调用并传入 name 参数以获取具体模板。");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ex.Message);
            }
        }

        JsonObject? argsObj = null;
        var argsStr = arguments.GetStringOrDefault("arguments");
        if (!string.IsNullOrWhiteSpace(argsStr))
        {
            try
            {
                argsObj = JsonNode.Parse(argsStr) as JsonObject;
            }
            catch (JsonException)
            {
                return ToolResult.Fail($"无效的 JSON 参数: {argsStr}");
            }
        }

        try
        {
            return ToolResult.Ok(await client.GetPromptAsync(name, argsObj, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex.Message);
        }
    }
}
