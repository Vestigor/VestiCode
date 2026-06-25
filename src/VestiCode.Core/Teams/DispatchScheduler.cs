using VestiCode.Core.Tools;

namespace VestiCode.Core.Teams;

/// <summary>纯调度模式：双锁启用后剥夺 Lead 的读写/命令工具，并注入 10 阶段工作流指引。</summary>
public sealed class DispatchScheduler
{
    private static readonly HashSet<string> DispatchDenyTools = new(StringComparer.Ordinal)
    {
        "read_file", "write_file", "edit_file", "run_command",
    };

    private bool _lock1; // TUI 开关
    private bool _lock2; // 配置/CLI 标志

    public bool IsActive => _lock1 && _lock2;

    public bool Lock1 => _lock1;

    public void SetLock1(bool enabled) => _lock1 = enabled;

    public void SetLock2(bool enabled) => _lock2 = enabled;

    /// <summary>10 阶段指挥官工作流文本（团队 Lead 在 DispatchMode 下复用）。</summary>
    public static string WorkflowText => Workflow;

    /// <summary>调度模式下移除读写/执行类工具。</summary>
    public IReadOnlyList<ToolDefinition> FilterTools(IReadOnlyList<ToolDefinition> tools)
        => IsActive ? tools.Where(t => !DispatchDenyTools.Contains(t.Name)).ToList() : tools;

    public string GetWorkflowInstructions() => IsActive ? Workflow : "";

    private const string Workflow =
        """
        [纯调度模式] 你是指挥官（Lead）。你只能：
        - 使用 sub_agent 或 team 工具分配工作
        - 使用 team_send_message 与成员通信
        - 使用 team_create_task / team_list_tasks 管理任务
        - 终止成员、查看结果、综合报告

        工作流程：
        1. 理解需求 → 2. 模块拆分 → 3. 依赖分析 → 4. 人员匹配 → 5. 任务委派
        → 6. 进度监控 → 7. 增量收集 → 8. 质量验证 → 9. 冲突仲裁 → 10. 综合报告

        不要自己读文件、写代码或执行命令——这些是成员的工作。
        """;
}
