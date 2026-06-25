using System.Text;
using System.Text.Json.Nodes;

namespace VestiCode.Core.Mcp;

/// <summary>Streamable HTTP 传输：POST /mcp，响应可为 SSE 流或纯 JSON。</summary>
public sealed class HttpTransport(
    string url,
    IReadOnlyDictionary<string, string> headers,
    double timeoutSeconds) : IMcpTransport
{
    private readonly string _url = url.TrimEnd('/') + "/mcp";
    private readonly Lock _idGate = new();
    private HttpClient? _client;
    private int _nextId = 1;

    public bool IsConnected => _client is not null;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        foreach (var (k, v) in headers)
        {
            _client.DefaultRequestHeaders.TryAddWithoutValidation(k, v);
        }
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        _client?.Dispose();
        _client = null;
        return Task.CompletedTask;
    }

    public async Task<JsonRpc.Response> SendRequestAsync(string method, JsonObject? @params, CancellationToken cancellationToken = default)
    {
        if (_client is null)
        {
            throw new InvalidOperationException("传输未连接");
        }

        int id;
        lock (_idGate)
        {
            id = _nextId++;
        }

        using var content = new StringContent(JsonRpc.Encode(new JsonRpc.Request(method, @params, id)), Encoding.UTF8, "application/json");
        using var resp = await _client.PostAsync(_url, content, cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new IOException($"MCP HTTP {(int)resp.StatusCode}");
        }

        var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (line.StartsWith("data: ", StringComparison.Ordinal)
                    && JsonRpc.DecodeResponse(line["data: ".Length..]) is { } sseResp && sseResp.Id == id)
                {
                    return sseResp;
                }
            }
            throw new IOException("SSE 流未返回匹配的响应");
        }

        var text = (await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
        return JsonRpc.DecodeResponse(text) ?? throw new IOException("无法解析 MCP 响应");
    }

    public async Task SendNotificationAsync(string method, JsonObject? @params = null, CancellationToken cancellationToken = default)
    {
        if (_client is null)
        {
            return;
        }
        using var content = new StringContent(JsonRpc.Encode(new JsonRpc.Notification(method, @params)), Encoding.UTF8, "application/json");
        using var resp = await _client.PostAsync(_url, content, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
    }
}
