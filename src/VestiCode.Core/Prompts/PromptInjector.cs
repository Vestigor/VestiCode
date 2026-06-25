namespace VestiCode.Core.Prompts;

/// <summary>
/// 每轮动态注入会话级状态指令（plan-only 提醒、一次性注入）。
/// 注入用 user 角色 + <c>[VestiCode]</c> 前缀，让模型当作系统级上下文。
/// </summary>
public sealed class PromptInjector
{
    /// <summary>plan-only 模式下完整注入的轮次间隔（其余轮用精简版）。</summary>
    private const int FullInjectionInterval = 3;

    private readonly List<string> _pending = [];
    private bool _planOnly;

    public void SetPlanOnly(bool enabled) => _planOnly = enabled;

    /// <summary>加入一次性注入（仅下一轮生效）。</summary>
    public void QueueInjection(string text) => _pending.Add($"[VestiCode] {text}");

    /// <summary>返回该轮的注入消息，无则返回 null。</summary>
    public string? BuildInjection(int roundNumber)
    {
        var parts = new List<string>(_pending);
        _pending.Clear();

        if (_planOnly)
        {
            var full = roundNumber == 1 || roundNumber % FullInjectionInterval == 0;
            var text = PromptModuleLoader.LoadInjection(full ? "plan-mode" : "plan-mode-slim");
            if (!string.IsNullOrEmpty(text))
            {
                parts.Add(text);
            }
        }

        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }
}
