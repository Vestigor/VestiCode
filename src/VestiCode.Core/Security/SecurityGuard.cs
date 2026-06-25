using System.Text.Json.Nodes;
using VestiCode.Core.Tools;

namespace VestiCode.Core.Security;

/// <summary>安全检查结果。</summary>
/// <param name="Decision">放行 / 拒绝 / 需确认。</param>
/// <param name="Reason">拒绝原因（放行/确认时为空）。</param>
public readonly record struct SecurityCheck(SecurityDecision Decision, string Reason = "");

/// <summary>
/// 安全管线编排：黑名单 → 路径沙箱 → 策略评估，逐个工具调用执行。
/// 失败原因会回灌给模型，让 Agent 据此调整。
/// </summary>
public sealed class SecurityGuard(SecurityPolicy policy, PathSandbox sandbox, ToolRegistry tools)
{
    private static readonly HashSet<string> PathTools = new(StringComparer.Ordinal)
    {
        "read_file", "write_file", "edit_file", "glob", "grep",
    };

    public SecurityPolicy Policy => policy;

    /// <summary>运行完整安全管线。</summary>
    public SecurityCheck Check(string toolName, JsonObject args)
    {
        var path = args.GetStringOrDefault("path") ?? args.GetStringOrDefault("file_path");
        var command = args.GetStringOrDefault("command") ?? args.GetStringOrDefault("cmd");

        // 1. 黑名单（始终生效）。
        if (command is not null && toolName == "run_command")
        {
            var blocked = CommandBlacklist.Check(command);
            if (blocked is not null)
            {
                return new SecurityCheck(SecurityDecision.Deny, blocked);
            }
        }

        // 2. 路径沙箱。
        if (path is not null && PathTools.Contains(toolName))
        {
            var (safe, message) = sandbox.Validate(path);
            if (!safe)
            {
                return new SecurityCheck(SecurityDecision.Deny, message);
            }
        }

        // 3. 策略评估（按工具真实 Category 判定读/写：MCP 只读工具也能在 Normal 档放行）。
        var isRead = (tools.Get(toolName)?.Category ?? ToolCategory.Write) == ToolCategory.Read;
        var action = policy.Evaluate(toolName, path, command, isRead);
        return action switch
        {
            RuleAction.Allow => new SecurityCheck(SecurityDecision.Allow),
            RuleAction.Deny => new SecurityCheck(SecurityDecision.Deny, BuildDenyReason(toolName, path, command)),
            _ => new SecurityCheck(SecurityDecision.Ask),
        };
    }

    /// <summary>构造 HITL 提示文本。</summary>
    public string BuildHitlPrompt(string toolName, JsonObject args) =>
        policy.ToHitlPrompt(toolName, ExtractArgs(args));

    /// <summary>应用 HITL 决策（生成会话/永久规则）。</summary>
    public void ApplyHitl(HitlDecision decision, string toolName, JsonObject args)
    {
        var path = args.GetStringOrDefault("path") ?? args.GetStringOrDefault("file_path");
        var command = args.GetStringOrDefault("command") ?? args.GetStringOrDefault("cmd");
        policy.HitlToRule(decision, toolName, path, command);
    }

    public void SetLevel(SecurityLevel level) => policy.SetLevel(level);

    private static string BuildDenyReason(string toolName, string? path, string? command)
    {
        var reason = $"安全策略拒绝: {toolName}";
        if (path is not null)
        {
            reason += $" (path={path})";
        }
        if (command is not null)
        {
            reason += $" (command={command})";
        }
        return reason;
    }

    private static IReadOnlyDictionary<string, string> ExtractArgs(JsonObject args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in args)
        {
            result[key] = value?.ToString() ?? "";
        }
        return result;
    }
}
