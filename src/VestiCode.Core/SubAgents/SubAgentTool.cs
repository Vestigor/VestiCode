using System.Text.Json.Nodes;
using VestiCode.Core.Conversation;
using VestiCode.Core.Tools;

namespace VestiCode.Core.SubAgents;

/// <summary>
/// sub_agent 工具：创建一个子工作器执行任务（角色式或 fork 式），同步跑到完成并返回报告。
/// 对应 Claude Code 的 Task 工具——父 Agent 阻塞等待子 Agent 的结构化报告作为工具结果。
/// </summary>
public sealed class SubAgentTool(
    SubAgentRunner runner,
    SubAgentManager manager,
    ConversationHistory parentHistory) : ITool
{
    public string Name => "sub_agent";

    public string Description
    {
        get
        {
            var roles = string.Join(", ", runner.Roles.Keys);
            return $"创建一个子工作器执行任务并返回其结构化报告。可用角色: {roles}" +
                   "（省略 role 则用 fork 模式继承当前对话）。适合把探索/规划等子任务委派出去。";
        }
    }

    public ToolCategory Category => ToolCategory.Write;

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new ToolParameter("task", "string", "要执行的任务描述"),
        new ToolParameter("role", "string", "预定义角色名（explorer/planner/general），省略则 fork 模式", Required: false),
    ];

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, CancellationToken cancellationToken = default)
    {
        var task = arguments.GetString("task");
        var role = arguments.GetStringOrDefault("role");
        role = string.IsNullOrWhiteSpace(role) ? null : role.Trim();

        if (role is not null && !runner.Roles.ContainsKey(role))
        {
            return ToolResult.Fail($"未知角色: {role}。可用: {string.Join(", ", runner.Roles.Keys)}");
        }

        var label = role ?? "fork";
        var subTask = manager.Create(role, task);
        subTask.Start();
        try
        {
            var report = await runner.RunAsync(subTask, parentHistory, cancellationToken).ConfigureAwait(false);
            return ToolResult.Ok($"[子 Agent '{label}' ({subTask.Id}) 完成，{subTask.RoundCount} 轮]\n\n{report}");
        }
        // 被 /tasks kill 终止（而非父级取消）：返回失败结果让父 Agent 继续，不中断整轮。
        catch (OperationCanceledException) when (subTask.CancelRequested && !cancellationToken.IsCancellationRequested)
        {
            subTask.MarkCancelled();
            return ToolResult.Fail($"子 Agent '{label}' ({subTask.Id}) 已被用户终止。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            subTask.Fail(ex.Message);
            return ToolResult.Fail($"子 Agent 执行失败: {ex.Message}");
        }
    }
}
