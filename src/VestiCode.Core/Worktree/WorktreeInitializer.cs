using Microsoft.Extensions.Logging;

namespace VestiCode.Core.Worktree;

/// <summary>
/// 新建 worktree 后的环境初始化：从主仓库复制本地配置、为大型依赖目录建符号链接，
/// 让子 Agent 在隔离工作目录里也能用到同一套配置与依赖。
/// </summary>
public sealed class WorktreeInitializer(string repoRoot, ILogger logger)
{
    // 体积大、可共享的依赖目录，优先建符号链接而非复制。
    private static readonly string[] SymlinkDirs = ["node_modules", ".venv", "venv", "bin", "obj"];

    public void Initialize(string worktreePath)
    {
        // 复制项目级 .vesticode/（本地配置 + 记忆，通常 gitignore，不随 worktree 自带）；
        // 排除 worktrees/ 以免递归复制。
        var srcVc = Path.Combine(repoRoot, ".vesticode");
        if (Directory.Exists(srcVc))
        {
            try
            {
                CopyDirExcept(srcVc, Path.Combine(worktreePath, ".vesticode"), "worktrees");
                logger.LogInformation("worktree 初始化：复制 .vesticode/ 配置与记忆");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "worktree 初始化：复制 .vesticode/ 失败");
            }
        }

        foreach (var dir in SymlinkDirs)
        {
            var src = Path.Combine(repoRoot, dir);
            var dst = Path.Combine(worktreePath, dir);
            if (Directory.Exists(src) && !Directory.Exists(dst) && !File.Exists(dst))
            {
                try
                {
                    Directory.CreateSymbolicLink(dst, src);
                    logger.LogInformation("worktree 初始化：链接 {Dir}", dir);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "worktree 初始化：链接 {Dir} 失败", dir);
                }
            }
        }
    }

    /// <summary>递归复制目录，跳过名为 <paramref name="exceptDirName"/> 的顶层子目录。</summary>
    private static void CopyDirExcept(string src, string dst, string exceptDirName)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.EnumerateFiles(src))
        {
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: false);
        }
        foreach (var dir in Directory.EnumerateDirectories(src))
        {
            var name = Path.GetFileName(dir);
            if (string.Equals(name, exceptDirName, StringComparison.Ordinal))
            {
                continue;
            }
            CopyDirExcept(dir, Path.Combine(dst, name), exceptDirName);
        }
    }
}
