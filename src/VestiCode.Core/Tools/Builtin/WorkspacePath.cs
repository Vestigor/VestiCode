namespace VestiCode.Core.Tools.Builtin;

/// <summary>
/// 工作目录内的路径解析与基础越界防护。
/// 完整的安全策略（黑名单、三档权限、HITL）在 Phase 2 的 Security 模块实现，
/// 这里只做最基本的“拒绝绝对路径 / 拒绝越出工作目录”兜底。
/// </summary>
public static class WorkspacePath
{
    /// <summary>把相对路径解析为当前工作目录内的绝对路径；非法路径抛 <see cref="ArgumentException"/>。</summary>
    public static string Resolve(string path) => Resolve(path, Directory.GetCurrentDirectory());

    /// <summary>同上，但以指定 <paramref name="root"/> 为根（团队成员各自的 worktree 用）。</summary>
    public static string Resolve(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("路径不能为空");
        }

        if (Path.IsPathRooted(path))
        {
            throw new ArgumentException($"不允许绝对路径: {path}");
        }

        var full = Path.GetFullPath(Path.Combine(root, path));

        // 规范化后必须仍在工作目录边界内，阻止 ".." 遍历越界。
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSep, StringComparison.Ordinal) &&
            !string.Equals(full, root, StringComparison.Ordinal))
        {
            throw new ArgumentException($"路径遍历不被允许: {path}");
        }

        return full;
    }
}
