using System.Text;
using VestiCode.Core.Skills;

namespace VestiCode.Core.Commands.Builtin;

/// <summary>/skill — 列出 / 激活 / 停用 Skill。</summary>
public sealed class SkillCommand(SkillRegistry registry) : ICommand
{
    public string Name => "skill";
    public IReadOnlyList<string> Aliases => ["skills"];
    public string Description => "管理 Skill";
    public string Usage => "/skill <list | <name> 激活 | off <name> 停用>";

    public Task<CommandResult> ExecuteAsync(IReadOnlyList<string> args, IUiControl ui)
    {
        if (args.Count == 0 || args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(CommandResult.Show(FormatList()));
        }

        if (args[0].Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Count < 2)
            {
                return Task.FromResult(CommandResult.Show("用法: /skill off <name>"));
            }
            return Task.FromResult(CommandResult.Show(
                registry.Deactivate(args[1]) ? $"已停用 Skill: {args[1]}" : $"Skill 未激活: {args[1]}"));
        }

        // 激活：/skill <name>
        var name = args[0];
        var skill = registry.Activate(name);
        if (skill is null)
        {
            return Task.FromResult(CommandResult.Show($"Skill '{name}' 不存在。用 /skill list 查看。"));
        }
        return Task.FromResult(CommandResult.Show(
            $"已激活 Skill '{name}'，其 SOP 指令已固定到上下文。下一条消息起生效。"));
    }

    private string FormatList()
    {
        var available = registry.ListAvailable();
        if (available.Count == 0)
        {
            return "暂无可用 Skill。";
        }
        var active = registry.Activated.Select(s => s.Meta.Name).ToHashSet(StringComparer.Ordinal);
        var sb = new StringBuilder("可用 Skill：\n");
        foreach (var meta in available)
        {
            var mark = active.Contains(meta.Name) ? "●" : "○";
            sb.Append($"  {mark} {meta.Name,-12} {meta.Description}\n");
        }
        sb.Append("\n● = 已激活；用 /skill <name> 激活，/skill off <name> 停用。");
        return sb.ToString().TrimEnd();
    }
}
