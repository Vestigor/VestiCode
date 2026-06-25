namespace VestiCode.Core.SubAgents;

/// <summary>子任务状态。</summary>
public enum TaskStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>子 Agent 角色（解析自 frontmatter）。</summary>
public sealed class SubAgentRole
{
    public required string Name { get; init; }
    public string Description { get; init; } = "";

    /// <summary>允许的工具；null = 除全局禁用外全部。</summary>
    public IReadOnlyList<string>? ToolsAllow { get; init; }

    public IReadOnlyList<string> ToolsDeny { get; init; } = [];
    public int MaxRounds { get; init; } = 5;
    public string Permission { get; init; } = "normal";

    /// <summary>角色 SOP（Markdown 正文）。</summary>
    public string SystemPrompt { get; init; } = "";
}

/// <summary>一次子 Agent 任务。</summary>
public sealed class SubAgentTask
{
    private readonly CancellationTokenSource _cts = new();

    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
    public string? Role { get; init; } // null = fork 模式
    public required string Task { get; init; }
    public TaskStatus Status { get; private set; } = TaskStatus.Queued;
    public string Result { get; private set; } = "";
    public int RoundCount { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>本任务的取消令牌（由 <see cref="Cancel"/> 触发，运行器与之联动）。</summary>
    public CancellationToken CancelToken => _cts.Token;

    /// <summary>是否已请求终止（区分父级取消与 /tasks kill）。</summary>
    public bool CancelRequested => _cts.IsCancellationRequested;

    public void Start()
    {
        Status = TaskStatus.Running;
        StartedAt = DateTimeOffset.UtcNow;
    }

    public void Complete(string result, int rounds)
    {
        if (Status == TaskStatus.Cancelled)
        {
            return; // 已被终止，不覆盖
        }
        Status = TaskStatus.Completed;
        Result = result;
        RoundCount = rounds;
        FinishedAt = DateTimeOffset.UtcNow;
    }

    public void Fail(string error)
    {
        if (Status == TaskStatus.Cancelled)
        {
            return;
        }
        Status = TaskStatus.Failed;
        Result = error;
        FinishedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>请求终止：触发取消令牌；尚未运行（排队中）则立即标记为已终止。</summary>
    public void Cancel()
    {
        _cts.Cancel();
        if (Status == TaskStatus.Queued)
        {
            MarkCancelled();
        }
    }

    /// <summary>运行停止后落定为已终止状态（仅在排队/运行中时生效）。</summary>
    public void MarkCancelled()
    {
        if (Status is TaskStatus.Queued or TaskStatus.Running)
        {
            Status = TaskStatus.Cancelled;
            FinishedAt = DateTimeOffset.UtcNow;
        }
    }
}
