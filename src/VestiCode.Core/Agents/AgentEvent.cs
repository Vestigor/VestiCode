using System.Text.Json.Nodes;
using VestiCode.Core.Llm;
using VestiCode.Core.Security;
using VestiCode.Core.Tools;

namespace VestiCode.Core.Agents;

/// <summary>
/// Agent 循环对外发出的事件。它是循环与 UI 之间的契约：
/// UI 只消费事件、不感知循环内部，从而解耦（也驱动 TUI 的动画状态行）。
/// </summary>
public abstract record AgentEvent;

/// <summary>新一轮 ReAct 回合开始。</summary>
public sealed record RoundStartEvent(int RoundNumber, int MaxRounds) : AgentEvent;

/// <summary>模型思考/推理增量（暗色渲染）。</summary>
public sealed record ThinkingEvent(string Text, string Label = "Thinking") : AgentEvent;

/// <summary>模型输出的一段增量正文。</summary>
public sealed record TextDeltaEvent(string Text) : AgentEvent;

/// <summary>模型请求调用一个工具。</summary>
public sealed record ToolCallEvent(ToolCall Call) : AgentEvent;

/// <summary>工具执行完毕，返回结果（Arguments 随结果带出，避免同名工具多次调用时参数错位）。</summary>
public sealed record ToolResultEvent(string ToolName, JsonObject Arguments, ToolResult Result) : AgentEvent;

/// <summary>工具被安全策略拦截。</summary>
public sealed record ToolBlockedEvent(string ToolName, string Reason) : AgentEvent;

/// <summary>
/// 人在回路：需要用户审批才能继续执行工具。
/// 消费方处理本事件时应弹出确认并把决策写入 <see cref="Decision"/>，再请求下一个事件，
/// 循环会在恢复时读取该决策（详见 AgentLoop 注释）。
/// </summary>
public sealed record HitlRequestEvent(
    string ToolName,
    JsonObject Args,
    string Prompt,
    TaskCompletionSource<HitlVerdict> Decision) : AgentEvent;

/// <summary>token 用量上报（供状态行计数）。</summary>
public sealed record UsageEvent(int InputTokens, int OutputTokens) : AgentEvent;

/// <summary>上下文用量越过 70% 警告线（尚未压缩）。</summary>
public sealed record ContextWarningEvent(int EstimatedTokens, int ContextWindow) : AgentEvent;

/// <summary>上下文被压缩（层2 结构化摘要）。</summary>
public sealed record CompactionEvent(int MessagesCompressed, int TokensSaved) : AgentEvent;

/// <summary>Agent 循环终止及其原因。</summary>
public sealed record AgentDoneEvent(AgentDoneReason Reason) : AgentEvent;

/// <summary>不可恢复的错误。</summary>
public sealed record ErrorEvent(string Message) : AgentEvent;

/// <summary>循环终止原因。</summary>
public enum AgentDoneReason
{
    /// <summary>模型不再请求工具，正常完成。</summary>
    NoToolCall,

    /// <summary>达到最大轮次上限。</summary>
    MaxRounds,

    /// <summary>被用户取消。</summary>
    Cancelled,
}
