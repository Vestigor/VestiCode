using System.Text.Json.Nodes;
using VestiCode.Core.Mcp;

namespace VestiCode.UnitTests;

public sealed class McpTests
{
    [Fact]
    public void JsonRpc_EncodeRequest_AndDecodeResponse()
    {
        var line = JsonRpc.Encode(new JsonRpc.Request("tools/list", new JsonObject { ["x"] = 1 }, 7));
        Assert.Contains("\"method\":\"tools/list\"", line);
        Assert.Contains("\"id\":7", line);

        var resp = JsonRpc.DecodeResponse("""{"jsonrpc":"2.0","id":7,"result":{"tools":[]}}""");
        Assert.NotNull(resp);
        Assert.Equal(7, resp!.Id);
        Assert.Null(resp.Error);
    }

    [Fact]
    public void JsonRpc_DecodeResponse_IgnoresNonResponses()
    {
        // 通知（无 id、无 result/error）不是响应。
        Assert.Null(JsonRpc.DecodeResponse("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
    }

    [Fact]
    public async Task McpClient_Handshake_ListTools_CallTool()
    {
        var transport = new FakeTransport();
        var client = new McpClient(transport, "demo");

        await client.ConnectAsync();
        Assert.True(client.IsConnected);
        Assert.True(transport.SawInitialized);

        var tools = await client.ListToolsAsync();
        Assert.Single(tools);
        Assert.Equal("echo", tools[0]["name"]!.GetValue<string>());

        var result = await client.CallToolAsync("echo", new JsonObject { ["text"] = "hi" });
        Assert.Equal("echoed: hi", result);
    }

    /// <summary>脚本化的假传输：按方法返回固定响应。</summary>
    private sealed class FakeTransport : IMcpTransport
    {
        private int _id;
        public bool IsConnected { get; private set; }
        public bool SawInitialized { get; private set; }

        public Task ConnectAsync(CancellationToken ct = default)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public Task<JsonRpc.Response> SendRequestAsync(string method, JsonObject? @params, CancellationToken ct = default)
        {
            JsonNode result = method switch
            {
                "initialize" => new JsonObject { ["serverInfo"] = new JsonObject { ["name"] = "demo" } },
                "tools/list" => new JsonObject
                {
                    ["tools"] = new JsonArray { new JsonObject { ["name"] = "echo", ["description"] = "echo back" } },
                },
                "tools/call" => new JsonObject
                {
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = $"echoed: {@params?["arguments"]?["text"]?.GetValue<string>()}" },
                    },
                },
                _ => new JsonObject(),
            };
            return Task.FromResult(new JsonRpc.Response(++_id, result, null));
        }

        public Task SendNotificationAsync(string method, JsonObject? @params = null, CancellationToken ct = default)
        {
            if (method == "notifications/initialized")
            {
                SawInitialized = true;
            }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
