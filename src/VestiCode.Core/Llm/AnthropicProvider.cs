using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using VestiCode.Core.Configuration;
using VestiCode.Core.Tools;

namespace VestiCode.Core.Llm;

/// <summary>
/// Anthropic Messages API 的 Provider（SSE 流式 + tool_use/tool_result + extended thinking）。
/// 与 OpenAI 的关键差异：system 独立于 messages；工具结果以 tool_result 块放进 user 回合；
/// 同一 wire 角色的相邻消息须合并为一个回合（Anthropic 要求 user/assistant 交替）。
/// </summary>
public sealed class AnthropicProvider(
    ProviderOptions config,
    HttpClient httpClient,
    ILogger<AnthropicProvider> logger) : ILlmProvider
{
    private const int MaxTokens = 4096;
    private const string AnthropicVersion = "2023-06-01";

    public ProviderOptions Config => config;

    public bool SupportsThinking => true;

    public async IAsyncEnumerable<LlmStreamItem> ChatStreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var url = config.BaseUrl.TrimEnd('/') + "/v1/messages";

        var (wireMessages, systemText) = BuildMessages(messages);
        var body = new JsonObject
        {
            ["model"] = config.Model,
            ["max_tokens"] = MaxTokens,
            ["stream"] = true,
            ["messages"] = wireMessages,
        };
        if (!string.IsNullOrEmpty(systemText))
        {
            body["system"] = systemText;
        }
        if (tools is { Count: > 0 })
        {
            body["tools"] = BuildTools(tools);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-api-key", config.ApiKey);
        request.Headers.Add("anthropic-version", AnthropicVersion);

        HttpResponseMessage? response = null;
        string? sendError = null;
        try
        {
            response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Anthropic 请求发送失败");
            sendError = ex.Message;
        }

        if (sendError is not null)
        {
            yield return new StreamError($"请求失败: {sendError}");
            yield break;
        }

        using (response)
        {
            if (!response!.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var snippet = errBody.Length > 500 ? errBody[..500] : errBody;
                yield return new StreamError(snippet, (int)response.StatusCode);
                yield break;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            // 按内容块 index 累积 tool_use 块。
            var toolAcc = new SortedDictionary<int, ToolCallBuilder>();
            var inputTokens = 0;
            var outputTokens = 0;

            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                // Anthropic 同时发 "event: <type>" 与 "data: <json>"；只处理 data 行，按 JSON 内 type 分派。
                if (!line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    continue;
                }

                var dataStr = line["data: ".Length..];
                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(dataStr);
                }
                catch (JsonException)
                {
                    continue;
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

                    switch (type)
                    {
                        case "message_start":
                            if (root.TryGetProperty("message", out var m)
                                && m.TryGetProperty("usage", out var u0)
                                && u0.TryGetProperty("input_tokens", out var it))
                            {
                                inputTokens = it.GetInt32();
                            }
                            break;

                        case "content_block_start":
                            var startIdx = root.GetProperty("index").GetInt32();
                            var block = root.GetProperty("content_block");
                            if (block.GetProperty("type").GetString() == "tool_use")
                            {
                                toolAcc[startIdx] = new ToolCallBuilder
                                {
                                    Id = block.TryGetProperty("id", out var bid) ? bid.GetString() ?? "" : "",
                                    Name = block.TryGetProperty("name", out var bn) ? bn.GetString() ?? "" : "",
                                };
                            }
                            break;

                        case "content_block_delta":
                            var idx = root.GetProperty("index").GetInt32();
                            var delta = root.GetProperty("delta");
                            var deltaType = delta.GetProperty("type").GetString();
                            if (deltaType == "text_delta")
                            {
                                var text = delta.GetProperty("text").GetString();
                                if (!string.IsNullOrEmpty(text))
                                {
                                    yield return new TextDelta(text);
                                }
                            }
                            else if (deltaType == "thinking_delta")
                            {
                                var think = delta.GetProperty("thinking").GetString();
                                if (!string.IsNullOrEmpty(think))
                                {
                                    yield return new ThinkingDelta(think);
                                }
                            }
                            else if (deltaType == "input_json_delta"
                                     && toolAcc.TryGetValue(idx, out var b))
                            {
                                b.Arguments.Append(delta.GetProperty("partial_json").GetString());
                            }
                            break;

                        case "message_delta":
                            if (root.TryGetProperty("usage", out var u1)
                                && u1.TryGetProperty("output_tokens", out var ot))
                            {
                                outputTokens = ot.GetInt32();
                            }
                            break;

                        case "message_stop":
                            break;
                    }
                }
            }

            if (inputTokens > 0 || outputTokens > 0)
            {
                yield return new UsageReport(inputTokens, outputTokens);
            }

            foreach (var builder in toolAcc.Values)
            {
                yield return new ToolCallReady(builder.Build());
            }
        }
    }

    private sealed class ToolCallBuilder
    {
        public string Id = "";
        public string Name = "";
        public StringBuilder Arguments { get; } = new();

        public ToolCall Build()
        {
            JsonObject parsed;
            var argStr = Arguments.ToString();
            try
            {
                parsed = string.IsNullOrWhiteSpace(argStr)
                    ? new JsonObject()
                    : JsonNode.Parse(argStr) as JsonObject ?? new JsonObject();
            }
            catch (JsonException)
            {
                parsed = new JsonObject();
            }
            return new ToolCall(Id, Name, parsed);
        }
    }

    // -- 序列化为 Anthropic 线缆格式 -------------------------------------------

    /// <summary>构造 messages 数组与 system 字符串；合并相邻同角色回合。</summary>
    private static (JsonArray Messages, string SystemText) BuildMessages(IReadOnlyList<ChatMessage> messages)
    {
        var system = new StringBuilder();
        var array = new JsonArray();

        // 当前正在累积的回合（user / assistant）及其内容块。
        string? currentRole = null;
        JsonArray? currentBlocks = null;

        void Flush()
        {
            if (currentRole is not null && currentBlocks is { Count: > 0 })
            {
                array.Add(new JsonObject { ["role"] = currentRole, ["content"] = currentBlocks });
            }
            currentRole = null;
            currentBlocks = null;
        }

        void Append(string role, JsonNode blockNode)
        {
            if (currentRole != role)
            {
                Flush();
                currentRole = role;
                currentBlocks = [];
            }
            currentBlocks!.Add(blockNode);
        }

        foreach (var msg in messages)
        {
            switch (msg.Role)
            {
                case ChatRole.System:
                    if (!string.IsNullOrEmpty(msg.Text))
                    {
                        if (system.Length > 0)
                        {
                            system.Append("\n\n");
                        }
                        system.Append(msg.Text);
                    }
                    break;

                case ChatRole.User:
                    Append("user", TextBlock(msg.Text ?? ""));
                    break;

                case ChatRole.Assistant when msg.ToolCalls.Count > 0:
                    if (!string.IsNullOrEmpty(msg.Text))
                    {
                        Append("assistant", TextBlock(msg.Text));
                    }
                    foreach (var call in msg.ToolCalls)
                    {
                        Append("assistant", new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = call.Id,
                            ["name"] = call.Name,
                            ["input"] = call.Arguments.DeepClone(),
                        });
                    }
                    break;

                case ChatRole.Assistant:
                    Append("assistant", TextBlock(msg.Text ?? ""));
                    break;

                case ChatRole.Tool:
                    Append("user", new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = msg.ToolCallId ?? "",
                        ["content"] = msg.Text ?? "",
                    });
                    break;
            }
        }

        Flush();
        return (array, system.ToString());
    }

    private static JsonObject TextBlock(string text) => new() { ["type"] = "text", ["text"] = text };

    private static JsonArray BuildTools(IReadOnlyList<ToolDefinition> tools)
    {
        var array = new JsonArray();
        foreach (var tool in tools)
        {
            var properties = new JsonObject();
            var required = new JsonArray();
            foreach (var p in tool.Parameters)
            {
                properties[p.Name] = new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
                if (p.Required)
                {
                    required.Add(p.Name);
                }
            }

            array.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["required"] = required,
                },
            });
        }
        return array;
    }
}
