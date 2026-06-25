using System.Text;
using System.Text.Json.Nodes;
using VestiCode.Core.Tools;

namespace VestiCode.Core.Teams;

/// <summary>团队协作工具集（仅团队成员可见）：任务 CRUD + 点对点/广播消息。</summary>
public static class TeamTools
{
    public static IReadOnlyList<ITool> Create(SharedTaskList tasks, Mailbox mailbox, string memberName, IReadOnlyList<string> allMembers) =>
    [
        new CreateTaskTool(tasks),
        new ListTasksTool(tasks),
        new ViewTaskTool(tasks),
        new UpdateTaskTool(tasks),
        new SendMessageTool(mailbox, memberName),
        new BroadcastTool(mailbox, memberName, allMembers),
    ];

    private sealed class CreateTaskTool(SharedTaskList tasks) : ITool
    {
        public string Name => "team_create_task";
        public string Description => "在共享任务清单中创建新任务，可指定依赖的其他任务 ID。";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("name", "string", "任务名称"),
            new("description", "string", "任务描述"),
            new("depends_on", "string", "逗号分隔的依赖任务 ID", Required: false),
        ];
        public Task<ToolResult> ExecuteAsync(JsonObject a, CancellationToken ct = default)
        {
            var deps = (a.GetStringOrDefault("depends_on") ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var task = tasks.Create(a.GetString("name"), a.GetString("description"), deps);
            return Task.FromResult(ToolResult.Ok($"任务已创建: {task.Id} ({task.Name})"));
        }
    }

    private sealed class ListTasksTool(SharedTaskList tasks) : ITool
    {
        public string Name => "team_list_tasks";
        public string Description => "列出共享任务清单中的所有任务。";
        public ToolCategory Category => ToolCategory.Read;
        public IReadOnlyList<ToolParameter> Parameters => [];
        public Task<ToolResult> ExecuteAsync(JsonObject a, CancellationToken ct = default)
        {
            var all = tasks.ListAll();
            if (all.Count == 0)
            {
                return Task.FromResult(ToolResult.Ok("(无任务)"));
            }
            var sb = new StringBuilder("任务清单:\n");
            foreach (var t in all)
            {
                var icon = t.Status switch
                {
                    TeamTaskStatus.Pending => "⏳",
                    TeamTaskStatus.InProgress => "🔄",
                    TeamTaskStatus.Completed => "✅",
                    _ => "❌",
                };
                sb.Append($"  {icon} [{t.Id}] {t.Name} → {(string.IsNullOrEmpty(t.AssignedTo) ? "未分配" : t.AssignedTo)} ({t.Status})\n");
            }
            return Task.FromResult(ToolResult.Ok(sb.ToString().TrimEnd()));
        }
    }

    private sealed class ViewTaskTool(SharedTaskList tasks) : ITool
    {
        public string Name => "team_view_task";
        public string Description => "查看指定任务的详细信息。";
        public ToolCategory Category => ToolCategory.Read;
        public IReadOnlyList<ToolParameter> Parameters => [new("task_id", "string", "任务 ID")];
        public Task<ToolResult> ExecuteAsync(JsonObject a, CancellationToken ct = default)
        {
            var t = tasks.Get(a.GetString("task_id"));
            if (t is null)
            {
                return Task.FromResult(ToolResult.Fail($"任务不存在: {a.GetString("task_id")}"));
            }
            return Task.FromResult(ToolResult.Ok(
                $"任务: {t.Id}\n名称: {t.Name}\n描述: {t.Description}\n状态: {t.Status}\n" +
                $"分配: {(string.IsNullOrEmpty(t.AssignedTo) ? "未分配" : t.AssignedTo)}\n" +
                $"依赖: {(t.DependsOn.Count > 0 ? string.Join(", ", t.DependsOn) : "无")}\n结果: {(string.IsNullOrEmpty(t.Result) ? "(无)" : t.Result)}"));
        }
    }

    private sealed class UpdateTaskTool(SharedTaskList tasks) : ITool
    {
        public string Name => "team_update_task";
        public string Description => "更新任务状态或结果。";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("task_id", "string", "任务 ID"),
            new("status", "string", "新状态: pending/in_progress/completed/failed"),
            new("result", "string", "任务结果文本", Required: false),
        ];
        public Task<ToolResult> ExecuteAsync(JsonObject a, CancellationToken ct = default)
        {
            if (!Enum.TryParse<TeamTaskStatus>(a.GetString("status").Replace("_", ""), ignoreCase: true, out var st))
            {
                return Task.FromResult(ToolResult.Fail($"无效状态: {a.GetString("status")}"));
            }
            var t = tasks.Update(a.GetString("task_id"), st, a.GetStringOrDefault("result") ?? "");
            return Task.FromResult(t is null
                ? ToolResult.Fail($"任务不存在: {a.GetString("task_id")}")
                : ToolResult.Ok($"任务 {t.Id} 已更新为 {st}"));
        }
    }

    private sealed class SendMessageTool(Mailbox mailbox, string sender) : ITool
    {
        public string Name => "team_send_message";
        public string Description => "向指定成员发送点对点消息。";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("to", "string", "接收者名称"),
            new("content", "string", "消息内容"),
        ];
        public Task<ToolResult> ExecuteAsync(JsonObject a, CancellationToken ct = default)
        {
            mailbox.Send(new TeamMessage { From = sender, To = a.GetString("to"), Content = a.GetString("content") });
            return Task.FromResult(ToolResult.Ok($"消息已发送给 {a.GetString("to")}"));
        }
    }

    private sealed class BroadcastTool(Mailbox mailbox, string sender, IReadOnlyList<string> all) : ITool
    {
        public string Name => "team_broadcast";
        public string Description => "向所有团队成员广播消息。";
        public IReadOnlyList<ToolParameter> Parameters => [new("content", "string", "广播内容")];
        public Task<ToolResult> ExecuteAsync(JsonObject a, CancellationToken ct = default)
        {
            mailbox.Broadcast(new TeamMessage { From = sender, Type = MessageType.Broadcast, Content = a.GetString("content") }, all);
            return Task.FromResult(ToolResult.Ok($"已广播给 {Math.Max(0, all.Count - 1)} 位成员"));
        }
    }
}
