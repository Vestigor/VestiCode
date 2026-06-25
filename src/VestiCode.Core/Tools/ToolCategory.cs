namespace VestiCode.Core.Tools;

/// <summary>
/// 工具分类，决定 Agent 循环的执行批次策略。
/// </summary>
public enum ToolCategory
{
    /// <summary>只读工具：可与其它只读工具并发执行。</summary>
    Read,

    /// <summary>写入工具：必须串行执行，避免相互冲突。</summary>
    Write,
}
