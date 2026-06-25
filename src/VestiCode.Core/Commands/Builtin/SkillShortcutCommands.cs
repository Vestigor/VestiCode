using VestiCode.Core.Skills;

namespace VestiCode.Core.Commands.Builtin;

/// <summary>激活某 Skill 并注入触发提示的快捷命令基类。</summary>
public abstract class SkillShortcutCommand(SkillRegistry registry, string skillName, string trigger) : ICommand
{
    public abstract string Name { get; }
    public abstract string Description { get; }

    public Task<CommandResult> ExecuteAsync(IReadOnlyList<string> args, IUiControl ui)
    {
        var skill = registry.Activate(skillName);
        if (skill is null)
        {
            return Task.FromResult(CommandResult.Show($"Skill '{skillName}' 不存在。"));
        }
        var extra = args.Count > 0 ? "\n补充要求：" + string.Join(' ', args) : "";
        return Task.FromResult(CommandResult.Inject($"{trigger}{extra}"));
    }
}

/// <summary>/commit — 激活 commit Skill 并生成提交信息。</summary>
public sealed class CommitCommand(SkillRegistry registry)
    : SkillShortcutCommand(registry, "commit", "请按 commit Skill 的 SOP，分析暂存区变更并生成规范的提交信息。")
{
    public override string Name => "commit";
    public override string Description => "激活 commit Skill 并生成 Conventional Commits 提交信息";
}

/// <summary>/test — 激活 test Skill 并生成/运行测试。</summary>
public sealed class TestCommand(SkillRegistry registry)
    : SkillShortcutCommand(registry, "test", "请按 test Skill 的 SOP，分析变更并生成或运行相关测试。")
{
    public override string Name => "test";
    public override string Description => "激活 test Skill 并生成/运行测试";
}
