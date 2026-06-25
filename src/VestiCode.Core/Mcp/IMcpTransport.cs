using System.Text.Json.Nodes;

namespace VestiCode.Core.Mcp;

/// <summary>MCP 传输抽象（stdio / HTTP / 未来传输实现此接口）。</summary>
public interface IMcpTransport : IAsyncDisposable
{
    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task DisconnectAsync();

    /// <summary>发送请求并等待匹配的响应。</summary>
    Task<JsonRpc.Response> SendRequestAsync(string method, JsonObject? @params, CancellationToken cancellationToken = default);

    /// <summary>发送单向通知（不等响应）。</summary>
    Task SendNotificationAsync(string method, JsonObject? @params = null, CancellationToken cancellationToken = default);
}
