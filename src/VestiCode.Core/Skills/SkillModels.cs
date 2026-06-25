namespace VestiCode.Core.Skills;

/// <summary>Skill 执行模式。</summary>
public enum SkillMode
{
    /// <summary>共享主对话上下文，结果留在历史里。</summary>
    Shared,

    /// <summary>独立对话，结果摘要回流（隔离执行，本版按共享方式固定指令）。</summary>
    Isolated,
}

/// <summary>历史携带策略（隔离模式用）。</summary>
public enum HistoryCarry
{
    Full,
    Recent,
    None,
}

/// <summary>Skill 元数据（解析自 YAML frontmatter）。</summary>
public sealed class SkillMeta
{
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public SkillMode Mode { get; init; } = SkillMode.Shared;
    public string? Model { get; init; }

    /// <summary>工具白名单：null = 全部工具；[] = 无工具。</summary>
    public IReadOnlyList<string>? Tools { get; init; }

    public HistoryCarry HistoryCarry { get; init; } = HistoryCarry.Full;
    public int RecentCount { get; init; } = 10;

    /// <summary>来源文件路径（磁盘 Skill 用于热重载）；内置为资源名。</summary>
    public string Source { get; init; } = "";
}

/// <summary>完整加载的 Skill（元数据 + 正文 SOP 指令）。</summary>
public sealed class SkillDefinition
{
    public required SkillMeta Meta { get; init; }
    public required string Body { get; init; }
}
