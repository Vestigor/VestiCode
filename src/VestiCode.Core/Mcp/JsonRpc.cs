using System.Text.Json;
using System.Text.Json.Nodes;

namespace VestiCode.Core.Mcp;

/// <summary>JSON-RPC 2.0 消息类型与编解码（MCP 协议基础）。</summary>
public static class JsonRpc
{
    public const string Version = "2.0";
    public const string McpProtocolVersion = "2024-11-05";

    public sealed record Request(string Method, JsonObject? Params, int Id);

    public sealed record Response(int Id, JsonNode? Result, JsonObject? Error);

    public sealed record Notification(string Method, JsonObject? Params);

    /// <summary>把请求编码为单行 JSON。</summary>
    public static string Encode(Request req)
    {
        var obj = new JsonObject { ["jsonrpc"] = Version, ["id"] = req.Id, ["method"] = req.Method };
        if (req.Params is not null)
        {
            obj["params"] = req.Params.DeepClone();
        }
        return obj.ToJsonString();
    }

    /// <summary>把通知编码为单行 JSON。</summary>
    public static string Encode(Notification notif)
    {
        var obj = new JsonObject { ["jsonrpc"] = Version, ["method"] = notif.Method };
        if (notif.Params is not null)
        {
            obj["params"] = notif.Params.DeepClone();
        }
        return obj.ToJsonString();
    }

    /// <summary>解码一行 JSON-RPC 响应（仅关心响应类型）。</summary>
    public static Response? DecodeResponse(string line)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }
        if (node is not JsonObject obj || !obj.ContainsKey("id"))
        {
            return null;
        }
        if (!obj.ContainsKey("result") && !obj.ContainsKey("error"))
        {
            return null;
        }

        var id = obj["id"]?.GetValue<int>() ?? 0;
        var error = obj["error"] as JsonObject;
        return new Response(id, obj["result"], error);
    }
}
