using VestiCode.Core.Memory;

namespace VestiCode.Core.Commands;

/// <summary>命令执行结果。</summary>
/// <param name="Message">要展示给用户的系统消息（可空）。</param>
/// <param name="InjectPrompt">要作为用户消息注入并触发 AI 的提示（可空）。</param>
public sealed record CommandResult(string? Message = null, string? InjectPrompt = null)
{
    public static CommandResult Show(string message) => new(Message: message);

    public static CommandResult Inject(string prompt) => new(InjectPrompt: prompt);

    public static readonly CommandResult None = new();
}

/// <summary>命令的简要信息（供 /help 展示）。</summary>
public sealed record CommandInfo(string Name, string Description, string Usage);

/// <summary>
/// 命令通过该接口与应用交互，避免直接耦合到具体 TUI 实现。
/// </summary>
public interface IUiControl
{
    IReadOnlyList<CommandInfo> ListCommands();
    /// <summary>立即向界面输出一行提示（用于长任务的实时进度，如 /team run）。</summary>
    void WriteNotice(string message);
    bool TogglePlanMode();
    bool GetPlanOnly();
    /// <summary>切换调度（指挥官）模式的 TUI 锁，返回该模式当前是否真正激活（需双锁同时开启）。</summary>
    bool ToggleDispatchMode();
    string SetSecurityLevel(string levelName);
    string GetSecurityLevel();
    int GetTokenCount();
    string GetProviderLabel();
    string GetApiKeyMasked();
    void ClearConversation();
    Task<string> TriggerCompressAsync();
    IReadOnlyList<SessionInfo> GetSessionList();
    string LoadSession(string sessionId);
    string NewSession();
    string DeleteSession(string sessionId);
}

/// <summary>一条斜杠命令。</summary>
public interface ICommand
{
    /// <summary>命令名（不含前导 <c>/</c>）。</summary>
    string Name { get; }

    /// <summary>别名。</summary>
    IReadOnlyList<string> Aliases => [];

    /// <summary>简介。</summary>
    string Description { get; }

    /// <summary>用法示例。</summary>
    string Usage => "";

    /// <summary>是否在补全/帮助中隐藏。</summary>
    bool Hidden => false;

    /// <summary>执行命令。</summary>
    Task<CommandResult> ExecuteAsync(IReadOnlyList<string> args, IUiControl ui);
}
