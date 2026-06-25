namespace VestiCode.Core.Llm;

/// <summary>
/// Provider 流式输出的单元。用强类型可辨识联合替代 mewcode 中
/// <c>str | ToolCall</c> + 魔法字符串前缀（<c>&lt;&lt;THINKING:&gt;&gt;</c> 等）的做法。
/// </summary>
public abstract record LlmStreamItem;

/// <summary>一段增量正文文本。</summary>
public sealed record TextDelta(string Text) : LlmStreamItem;

/// <summary>一段思考/推理增量（Claude thinking / DeepSeek reasoning），UI 以暗色渲染。</summary>
public sealed record ThinkingDelta(string Text, string Label = "Thinking") : LlmStreamItem;

/// <summary>一次完整的工具调用（在流结束时给出）。</summary>
public sealed record ToolCallReady(ToolCall Call) : LlmStreamItem;

/// <summary>token 用量上报（用于 TUI 状态行的 token 计数）。</summary>
public sealed record UsageReport(int InputTokens, int OutputTokens) : LlmStreamItem;

/// <summary>不可恢复的请求错误（HTTP 非 200 等）。</summary>
public sealed record StreamError(string Message, int? StatusCode = null) : LlmStreamItem;
