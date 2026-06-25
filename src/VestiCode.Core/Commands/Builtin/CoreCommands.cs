using System.Text;

namespace VestiCode.Core.Commands.Builtin;

/// <summary>/help — 列出可用命令。</summary>
public sealed class HelpCommand : ICommand
{
    public string Name => "help";
    public IReadOnlyList<string> Aliases => ["?"];
    public string Description => "显示所有可用命令";

    public Task<CommandResult> ExecuteAsync(IReadOnlyList<string> args, IUiControl ui)
    {
        var commands = ui.ListCommands();

        // /help <command> → 显示单条命令详情。
        if (args.Count > 0)
        {
            var name = args[0].TrimStart('/');
            var cmd = commands.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (cmd is null)
            {
                return Task.FromResult(CommandResult.Show($"未知命令: /{name}。输入 /help 查看全部。"));
            }
            var detail = $"/{cmd.Name}\n  {cmd.Description}";
            if (!string.IsNullOrEmpty(cmd.Usage))
            {
                detail += $"\n  用法: {cmd.Usage}";
            }
            return Task.FromResult(CommandResult.Show(detail));
        }

        var sb = new StringBuilder("可用命令：\n");
        foreach (var c in commands)
        {
            sb.Append($"  /{c.Name,-12} {c.Description}\n");
        }
        sb.Append("\n直接输入文字则交给 AI 处理；/help <命令> 查看子命令与用法；/exit 退出。");
        return Task.FromResult(CommandResult.Show(sb.ToString().TrimEnd()));
    }
}

/// <summary>/clear — 清空当前对话历史。</summary>
public sealed class ClearCommand : ICommand
{
    public string Name => "clear";
    public IReadOnlyList<string> Aliases => ["cls"];
    public string Description => "清空当前对话历史";

    public Task<CommandResult> ExecuteAsync(IReadOnlyList<string> args, IUiControl ui)
    {
        ui.ClearConversation();
        return Task.FromResult(CommandResult.Show("已清空对话历史。"));
    }
}

/// <summary>/status — 显示当前会话状态。</summary>
public sealed class StatusCommand : ICommand
{
    public string Name => "status";
    public string Description => "显示 Provider、token、安全档位等状态";

    public Task<CommandResult> ExecuteAsync(IReadOnlyList<string> args, IUiControl ui)
    {
        var text =
            $"Provider : {ui.GetProviderLabel()}\n" +
            $"API Key  : {ui.GetApiKeyMasked()}\n" +
            $"Tokens   : ~{ui.GetTokenCount()}（估算）\n" +
            $"安全档位 : {ui.GetSecurityLevel()}\n" +
            $"Plan-only: {(ui.GetPlanOnly() ? "开启" : "关闭")}";
        return Task.FromResult(CommandResult.Show(text));
    }
}

/// <summary>/mode — 切换安全档位。</summary>
public sealed class ModeCommand : ICommand
{
    public string Name => "mode";
    public string Description => "切换安全档位";
    public string Usage => "/mode <strict|normal|permissive>";

    public Task<CommandResult> ExecuteAsync(IReadOnlyList<string> args, IUiControl ui)
    {
        if (args.Count == 0)
        {
            return Task.FromResult(CommandResult.Show(
                $"当前档位: {ui.GetSecurityLevel()}。用法: /mode strict|normal|permissive"));
        }
        var result = ui.SetSecurityLevel(args[0]);
        return Task.FromResult(CommandResult.Show(result));
    }
}

/// <summary>/plan — 切换 plan-only 模式。</summary>
public sealed class PlanCommand : ICommand
{
    public string Name => "plan";
    public string Description => "切换 plan-only 模式（仅只读工具，写操作被拦截）";

    public Task<CommandResult> ExecuteAsync(IReadOnlyList<string> args, IUiControl ui)
    {
        var enabled = ui.TogglePlanMode();
        return Task.FromResult(CommandResult.Show(
            enabled ? "plan-only 已开启：仅 read_file/glob/grep 可用。" : "plan-only 已关闭：所有工具恢复可用。"));
    }
}

/// <summary>/dispatch — 切换双锁调度（指挥官）模式：激活后 Agent 失去读写/执行工具，只能委派。</summary>
public sealed class DispatchCommand : ICommand
{
    public string Name => "dispatch";
    public string Description => "切换调度模式（双锁，激活后只能委派）";
    public string Usage => "/dispatch（需配合启动参数 --dispatch）";

    public Task<CommandResult> ExecuteAsync(IReadOnlyList<string> args, IUiControl ui)
    {
        var active = ui.ToggleDispatchMode();
        return Task.FromResult(CommandResult.Show(active
            ? "调度模式已激活：read_file/write_file/edit_file/run_command 已停用，请用 sub_agent/team 委派工作。"
            : "调度模式 TUI 锁已切换；当前未完全激活（需同时以 --dispatch 启动开启第二把锁）。"));
    }
}

/// <summary>/permission — 显示当前权限档位。</summary>
public sealed class PermissionCommand : ICommand
{
    public string Name => "permission";
    public IReadOnlyList<string> Aliases => ["perm"];
    public string Description => "显示当前权限设置";

    public Task<CommandResult> ExecuteAsync(IReadOnlyList<string> args, IUiControl ui)
        => Task.FromResult(CommandResult.Show(
            $"当前安全档位: {ui.GetSecurityLevel()}\n" +
            "strict=仅白名单路径放行 / normal=读放行写询问 / permissive=仅黑名单拦截\n" +
            "用 /mode <档位> 切换；写操作触发确认时可选 [P]ermanent 写入持久化规则。"));
}

/// <summary>/compress — 手动触发上下文压缩。</summary>
public sealed class CompressCommand : ICommand
{
    public string Name => "compress";
    public IReadOnlyList<string> Aliases => ["compact"];
    public string Description => "手动触发上下文压缩（结构化摘要）";

    public async Task<CommandResult> ExecuteAsync(IReadOnlyList<string> args, IUiControl ui)
        => CommandResult.Show(await ui.TriggerCompressAsync().ConfigureAwait(false));
}

/// <summary>/review — 注入代码审查提示，交给 AI 执行。</summary>
public sealed class ReviewCommand : ICommand
{
    public string Name => "review";
    public string Description => "让 AI 审查代码改动并给出改进建议";
    public string Usage => "/review [文件或目录]";

    public Task<CommandResult> ExecuteAsync(IReadOnlyList<string> args, IUiControl ui)
    {
        var target = args.Count > 0 ? string.Join(' ', args) : "当前项目最近的改动";
        var prompt =
            $"请审查 {target}。步骤：\n" +
            "1. 用 glob/grep/read_file 了解相关代码；\n" +
            "2. 从正确性、可读性、错误处理、性能、安全等角度找出问题；\n" +
            "3. 按严重程度给出具体的、可操作的改进建议（含文件:行号）。";
        return Task.FromResult(CommandResult.Inject(prompt));
    }
}
