namespace VestiCode.Core.SubAgents;

/// <summary>子 Agent 工具过滤——三层防线，防止 A→B→C 递归链。</summary>
public static class ToolFilter
{
    /// <summary>子 Agent 中始终禁用（阻止递归创建子 Agent）。</summary>
    private static readonly HashSet<string> GlobalBlocked = new(StringComparer.Ordinal) { "sub_agent" };

    /// <summary>返回允许的工具名集合。</summary>
    public static IReadOnlyList<string> Filter(IEnumerable<string> toolNames, SubAgentRole? role)
    {
        var allowed = new HashSet<string>(toolNames, StringComparer.Ordinal);

        // 层1：全局禁用。
        allowed.ExceptWith(GlobalBlocked);

        // 层2：角色 allow/deny。
        if (role is not null)
        {
            if (role.ToolsAllow is not null)
            {
                allowed.IntersectWith(role.ToolsAllow);
            }
            allowed.ExceptWith(role.ToolsDeny);
        }

        return allowed.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }
}
