using System.Text.Json.Nodes;
using VestiCode.Core.Tools;

namespace VestiCode.Core.Skills;

/// <summary>
/// 系统级工具：按需激活 Skill 以获取 SOP 指令。始终可用，不受 Skill 工具白名单约束，
/// 从而支持 Skill 嵌套（一个 Skill 可触发另一个的激活）。
/// </summary>
public sealed class SkillTool(SkillRegistry registry) : ITool
{
    public string Name => "skill_loader";

    public string Description
    {
        get
        {
            var names = string.Join(", ", registry.ListAvailable().Take(20).Select(s => s.Name));
            return $"激活一个 Skill 以获取专业的 SOP 指令。可用 Skills: {names}";
        }
    }

    public ToolCategory Category => ToolCategory.Read;

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new ToolParameter("name", "string", "要激活的 Skill 名称"),
    ];

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, CancellationToken cancellationToken = default)
    {
        var name = arguments.GetString("name");
        var skill = registry.Activate(name);
        if (skill is null)
        {
            var available = string.Join(", ", registry.ListAvailable().Select(s => s.Name));
            return Task.FromResult(ToolResult.Fail($"Skill '{name}' 不存在。可用: {available}"));
        }

        var whitelist = skill.Meta.Tools is null ? "全部" : string.Join(", ", skill.Meta.Tools);
        return Task.FromResult(ToolResult.Ok(
            $"Skill '{name}' 已激活。\n\n模式: {skill.Meta.Mode}\n工具白名单: {whitelist}\n\n--- SOP 指令 ---\n{skill.Body}"));
    }
}
