using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using VestiCode.Core.Configuration;
using VestiCode.Core.Tools;

namespace VestiCode.Core.Llm;

/// <summary>
/// OpenAI Chat Completions 协议的 Provider（SSE 流式 + 工具调用）。
/// 同时服务 DeepSeek 等 OpenAI 兼容后端（含 reasoning_content）。
/// </summary>
public sealed class OpenAIProvider(
    ProviderOptions config,
    HttpClient httpClient,
    ILogger<OpenAIProvider> logger) : ILlmProvider
{
    public ProviderOptions Config => config;

    public bool SupportsThinking => true; // DeepSeek reasoning_content

    public async IAsyncEnumerable<LlmStreamItem> ChatStreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var url = config.BaseUrl.TrimEnd('/') + "/chat/completions";

        var body = new JsonObject
        {
            ["model"] = config.Model,
            ["stream"] = true,
            ["messages"] = BuildMessages(messages),
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
        };
        if (tools is { Count: > 0 })
        {
            body["tools"] = BuildTools(tools);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);

        // 发送请求（仅读到响应头即返回，以便流式读取响应体）。
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
            logger.LogError(ex, "OpenAI 请求发送失败");
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

            // 按 tool_calls 的 index 累积分片（OpenAI 把一次工具调用拆成多个增量）。
            var toolAcc = new SortedDictionary<int, ToolCallBuilder>();

            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0 || !line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    continue;
                }

                var dataStr = line["data: ".Length..];
                if (dataStr.Trim() == "[DONE]")
                {
                    break;
                }

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(dataStr);
                }
                catch (JsonException)
                {
                    continue; // 跳过不完整/非 JSON 行
                }

                using (doc)
                {
                    var root = doc.RootElement;

                    // 用量（最后一帧 choices 为空、带 usage）。
                    if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                    {
                        var inTok = usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
                        var outTok = usage.TryGetProperty("completion_tokens", out var ct) ? ct.GetInt32() : 0;
                        yield return new UsageReport(inTok, outTok);
                    }

                    if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                    {
                        continue;
                    }

                    var delta = choices[0].GetProperty("delta");

                    // DeepSeek 推理增量。
                    if (delta.TryGetProperty("reasoning_content", out var reasoning)
                        && reasoning.ValueKind == JsonValueKind.String)
                    {
                        var r = reasoning.GetString();
                        if (!string.IsNullOrEmpty(r))
                        {
                            yield return new ThinkingDelta(r, "Reasoning");
                        }
                    }

                    // 工具调用增量（累积，不在此处 yield）。
                    if (delta.TryGetProperty("tool_calls", out var tcDeltas)
                        && tcDeltas.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tc in tcDeltas.EnumerateArray())
                        {
                            AccumulateToolCall(toolAcc, tc);
                        }
                    }

                    // 正文增量。
                    if (delta.TryGetProperty("content", out var content)
                        && content.ValueKind == JsonValueKind.String)
                    {
                        var text = content.GetString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            yield return new TextDelta(text);
                        }
                    }
                }
            }

            // 流结束后，给出累积完成的工具调用。
            foreach (var builder in toolAcc.Values)
            {
                yield return new ToolCallReady(builder.Build());
            }
        }
    }

    // -- 累积工具调用分片 ------------------------------------------------------

    private static void AccumulateToolCall(SortedDictionary<int, ToolCallBuilder> acc, JsonElement tc)
    {
        var idx = tc.TryGetProperty("index", out var i) ? i.GetInt32() : 0;
        if (!acc.TryGetValue(idx, out var builder))
        {
            builder = new ToolCallBuilder();
            acc[idx] = builder;
        }

        if (tc.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
        {
            var s = id.GetString();
            if (!string.IsNullOrEmpty(s))
            {
                builder.Id = s;
            }
        }

        if (tc.TryGetProperty("function", out var func) && func.ValueKind == JsonValueKind.Object)
        {
            if (func.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                builder.Name += name.GetString();
            }
            if (func.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.String)
            {
                builder.Arguments.Append(args.GetString());
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

    // -- 序列化为 OpenAI 线缆格式 ----------------------------------------------

    private static JsonArray BuildMessages(IReadOnlyList<ChatMessage> messages)
    {
        var array = new JsonArray();
        foreach (var msg in messages)
        {
            switch (msg.Role)
            {
                case ChatRole.System:
                    array.Add(new JsonObject { ["role"] = "system", ["content"] = msg.Text ?? "" });
                    break;

                case ChatRole.User:
                    array.Add(new JsonObject { ["role"] = "user", ["content"] = msg.Text ?? "" });
                    break;

                case ChatRole.Assistant when msg.ToolCalls.Count > 0:
                    var toolCalls = new JsonArray();
                    foreach (var call in msg.ToolCalls)
                    {
                        toolCalls.Add(new JsonObject
                        {
                            ["id"] = call.Id,
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = call.Name,
                                ["arguments"] = call.Arguments.ToJsonString(),
                            },
                        });
                    }
                    array.Add(new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = msg.Text is { Length: > 0 } ? msg.Text : null,
                        ["tool_calls"] = toolCalls,
                    });
                    break;

                case ChatRole.Assistant:
                    array.Add(new JsonObject { ["role"] = "assistant", ["content"] = msg.Text ?? "" });
                    break;

                case ChatRole.Tool:
                    array.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = msg.ToolCallId ?? "",
                        ["name"] = msg.ToolName ?? "",
                        ["content"] = msg.Text ?? "",
                    });
                    break;
            }
        }
        return array;
    }

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
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = properties,
                        ["required"] = required,
                    },
                },
            });
        }
        return array;
    }
}
