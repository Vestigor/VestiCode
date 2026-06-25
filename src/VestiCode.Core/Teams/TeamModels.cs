namespace VestiCode.Core.Teams;

/// <summary>成员状态。</summary>
public enum MemberStatus { Idle, Busy, Done, Failed }

/// <summary>团队任务状态。</summary>
public enum TeamTaskStatus { Pending, InProgress, Completed, Failed }

/// <summary>团队消息类型。</summary>
public enum MessageType { Text, Lifecycle, Approval, Broadcast }

/// <summary>共享任务清单中的一条任务。</summary>
public sealed class TeamTask
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string AssignedTo { get; set; } = "";
    public List<string> DependsOn { get; set; } = [];
    public TeamTaskStatus Status { get; set; } = TeamTaskStatus.Pending;
    public string Result { get; set; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>团队消息。</summary>
public sealed class TeamMessage
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string From { get; set; } = "";
    public string To { get; set; } = ""; // "" = 广播
    public MessageType Type { get; init; } = MessageType.Text;
    public string Content { get; init; } = "";
    public string Summary { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>成员定义。</summary>
public sealed class MemberDef
{
    public required string Name { get; init; }
    public string Role { get; init; } = "";       // SubAgentRole 名
    public string Worktree { get; init; } = "";   // worktree 名
    public bool NeedsApproval { get; init; }
    public string Model { get; init; } = "";
}

/// <summary>团队定义。</summary>
public sealed class TeamDef
{
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public string LeadRole { get; init; } = "";
    public List<MemberDef> Members { get; init; } = [];
    public bool DispatchMode { get; init; }
    public int MaxRoundsPerMember { get; init; } = 10;
}
