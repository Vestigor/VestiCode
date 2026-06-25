using System.Text.Json.Nodes;

namespace VestiCode.Core.Mcp;

/// <summary>
/// MCP 客户端：通过传输层讲 JSON-RPC 2.0。
/// 生命周期：connect → initialize → initialized → discover → call。
/// </summary>
public sealed class McpClient(IMcpTransport transport, string serverName)
{
    public string ServerName => serverName;

    public bool IsConnected { get; private set; }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await transport.ConnectAsync(ct).ConfigureAwait(false);

        var init = await transport.SendRequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = JsonRpc.McpProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "VestiCode", ["version"] = "0.1.0" },
        }, ct).ConfigureAwait(false);

        if (init.Error is not null)
        {
            throw new IOException($"initialize 失败: {init.Error.ToJsonString()}");
        }

        await transport.SendNotificationAsync("notifications/initialized", null, ct).ConfigureAwait(false);
        IsConnected = true;
    }

    public async Task DisconnectAsync()
    {
        IsConnected = false;
        await transport.DisconnectAsync().ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<JsonObject>> ListToolsAsync(CancellationToken ct = default)
        => await ListAsync("tools/list", "tools", ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<JsonObject>> ListResourcesAsync(CancellationToken ct = default)
        => await ListAsync("resources/list", "resources", ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<JsonObject>> ListPromptsAsync(CancellationToken ct = default)
        => await ListAsync("prompts/list", "prompts", ct).ConfigureAwait(false);

    public async Task<string> CallToolAsync(string name, JsonObject arguments, CancellationToken ct = default)
    {
        var resp = await transport.SendRequestAsync("tools/call",
            new JsonObject { ["name"] = name, ["arguments"] = arguments.DeepClone() }, ct).ConfigureAwait(false);
        if (resp.Error is not null)
        {
            throw new IOException($"tools/call '{name}' 失败: {resp.Error.ToJsonString()}");
        }
        return ExtractText((resp.Result as JsonObject)?["content"] as JsonArray);
    }

    public async Task<string> ReadResourceAsync(string uri, CancellationToken ct = default)
    {
        var resp = await transport.SendRequestAsync("resources/read", new JsonObject { ["uri"] = uri }, ct).ConfigureAwait(false);
        if (resp.Error is not null)
        {
            throw new IOException($"resources/read '{uri}' 失败: {resp.Error.ToJsonString()}");
        }
        return ExtractText((resp.Result as JsonObject)?["contents"] as JsonArray);
    }

    public async Task<string> GetPromptAsync(string name, JsonObject? arguments, CancellationToken ct = default)
    {
        var prms = new JsonObject { ["name"] = name };
        if (arguments is not null)
        {
            prms["arguments"] = arguments.DeepClone();
        }
        var resp = await transport.SendRequestAsync("prompts/get", prms, ct).ConfigureAwait(false);
        if (resp.Error is not null)
        {
            throw new IOException($"prompts/get '{name}' 失败: {resp.Error.ToJsonString()}");
        }

        var messages = (resp.Result as JsonObject)?["messages"] as JsonArray ?? [];
        var parts = new List<string>();
        foreach (var m in messages.OfType<JsonObject>())
        {
            var role = m["role"]?.GetValue<string>() ?? "unknown";
            var content = m["content"] is JsonObject co ? co["text"]?.GetValue<string>() ?? "" : m["content"]?.ToString() ?? "";
            parts.Add($"[{role}]: {content}");
        }
        return string.Join("\n", parts);
    }

    private async Task<IReadOnlyList<JsonObject>> ListAsync(string method, string key, CancellationToken ct)
    {
        var resp = await transport.SendRequestAsync(method, null, ct).ConfigureAwait(false);
        if (resp.Error is not null)
        {
            throw new IOException($"{method} 失败: {resp.Error.ToJsonString()}");
        }
        var array = (resp.Result as JsonObject)?[key] as JsonArray ?? [];
        return array.OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone()).ToList();
    }

    private static string ExtractText(JsonArray? contents)
    {
        if (contents is null)
        {
            return "";
        }
        var parts = new List<string>();
        foreach (var item in contents.OfType<JsonObject>())
        {
            if (item["type"]?.GetValue<string>() == "text")
            {
                parts.Add(item["text"]?.GetValue<string>() ?? "");
            }
            else if (item["type"]?.GetValue<string>() == "resource" && item["resource"] is JsonObject res)
            {
                parts.Add(res["text"]?.GetValue<string>() ?? "");
            }
        }
        return string.Join("\n", parts);
    }
}
