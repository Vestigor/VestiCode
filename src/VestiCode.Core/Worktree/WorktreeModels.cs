namespace VestiCode.Core.Worktree;

/// <summary>一个 Git worktree 的信息。</summary>
public sealed record WorktreeInfo(
    string Name,
    string Path,
    string Branch,
    string HeadCommit = "",
    bool IsActive = false,
    bool HasChanges = false);
