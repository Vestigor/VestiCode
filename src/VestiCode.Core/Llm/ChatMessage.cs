using System.Text.Json.Nodes;

namespace VestiCode.Core.Llm;

/// <summary>对话消息角色。</summary>
public enum ChatRole
{
    System,
    User,
    Assistant,
    Tool,
}

/// <summary>模型请求的一次工具调用。</summary>
/// <param name="Id">调用 ID（用于把结果与调用配对）。</param>
/// <param name="Name">工具名。</param>
/// <param name="Arguments">参数对象（来自模型生成的 JSON）。</param>
public sealed record ToolCall(string Id, string Name, JsonObject Arguments);

/// <summary>
/// 协议无关的对话消息模型。消息<b>构造</b>与具体 Provider 无关；
/// 只有<b>序列化为线缆 JSON</b> 才因 Provider 而异（由各 Provider 负责）。
/// </summary>
public sealed record ChatMessage
{
    /// <summary>角色。</summary>
    public required ChatRole Role { get; init; }

    /// <summary>文本内容（工具调用消息可只带 ToolCalls 而无文本）。</summary>
    public string? Text { get; init; }

    /// <summary>Assistant 消息携带的工具调用（可多个）。</summary>
    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = [];

    /// <summary>Tool 消息：对应的工具调用 ID。</summary>
    public string? ToolCallId { get; init; }

    /// <summary>Tool 消息：工具名。</summary>
    public string? ToolName { get; init; }

    // -- 工厂方法（协议无关的构造） --------------------------------------------

    public static ChatMessage FromUser(string text) => new() { Role = ChatRole.User, Text = text };

    public static ChatMessage FromAssistant(string text) => new() { Role = ChatRole.Assistant, Text = text };

    public static ChatMessage FromSystem(string text) => new() { Role = ChatRole.System, Text = text };

    /// <summary>构造携带工具调用的 assistant 消息（可带前置文本）。</summary>
    public static ChatMessage FromToolCalls(IReadOnlyList<ToolCall> toolCalls, string? textPrefix = null) => new()
    {
        Role = ChatRole.Assistant,
        Text = string.IsNullOrEmpty(textPrefix) ? null : textPrefix,
        ToolCalls = toolCalls,
    };

    /// <summary>构造携带工具执行结果的 tool 消息。</summary>
    public static ChatMessage FromToolResult(string toolCallId, string toolName, string content) => new()
    {
        Role = ChatRole.Tool,
        ToolCallId = toolCallId,
        ToolName = toolName,
        Text = content,
    };
}
