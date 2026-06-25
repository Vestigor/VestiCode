using System.Collections.Concurrent;
using System.Text;

namespace VestiCode.Core.SubAgents;

/// <summary>跟踪子 Agent 任务（供 /tasks 查看）。</summary>
public sealed class SubAgentManager
{
    private readonly ConcurrentDictionary<string, SubAgentTask> _tasks = new();

    public SubAgentTask Create(string? role, string task)
    {
        var t = new SubAgentTask { Role = role, Task = task };
        _tasks[t.Id] = t;
        return t;
    }

    public IReadOnlyList<SubAgentTask> ListTasks() => _tasks.Values.ToList();

    /// <summary>终止运行中/排队中的子任务（支持 8 位 ID 前缀匹配）。</summary>
    public string Kill(string id)
    {
        var task = _tasks.TryGetValue(id, out var exact)
            ? exact
            : _tasks.Values.FirstOrDefault(t => t.Id.StartsWith(id, StringComparison.Ordinal));

        if (task is null)
        {
            return $"未找到任务: {id}";
        }
        if (task.Status is not (TaskStatus.Queued or TaskStatus.Running))
        {
            return $"任务 {task.Id} 已结束（{task.Status}），无需终止。";
        }
        task.Cancel();
        return $"已请求终止任务 {task.Id}。";
    }

    public string GetStatusSummary()
    {
        if (_tasks.IsEmpty)
        {
            return "没有子 Agent 任务。";
        }
        var sb = new StringBuilder("子 Agent 任务：\n");
        foreach (var t in _tasks.Values.OrderByDescending(x => x.StartedAt ?? DateTimeOffset.MinValue))
        {
            var icon = t.Status switch
            {
                TaskStatus.Queued => "⏳",
                TaskStatus.Running => "🔄",
                TaskStatus.Completed => "✅",
                TaskStatus.Failed => "❌",
                _ => "🚫",
            };
            var task = t.Task.Length > 60 ? t.Task[..60] : t.Task;
            sb.Append($"  {icon} {t.Id}  {t.Role ?? "fork"}  {task}\n");
        }
        return sb.ToString().TrimEnd();
    }
}
