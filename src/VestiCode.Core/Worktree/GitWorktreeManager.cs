using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace VestiCode.Core.Worktree;

/// <summary>
/// Git worktree 管理：创建 / 进入 / 退出 / 列出 / 状态 / 清理，用于子 Agent 的工作目录隔离。
/// worktree 统一放在仓库的 <c>.vesticode/worktrees/</c> 下；分支前缀 <c>vesticode/</c>。
/// 退出时若有未提交修改默认拒绝删除（需 force）。
/// </summary>
public sealed class GitWorktreeManager
{
    private readonly ILogger<GitWorktreeManager> _logger;
    private readonly string _repoRoot;
    private readonly string _worktreesDir;
    private readonly string _originalCwd;

    public GitWorktreeManager(ILogger<GitWorktreeManager> logger)
    {
        _logger = logger;
        _repoRoot = FindRepoRoot();
        _worktreesDir = Path.Combine(_repoRoot, ".vesticode", "worktrees");
        _originalCwd = Directory.GetCurrentDirectory();
    }

    public string Active { get; private set; } = ""; // "" = 主仓库

    /// <summary>主仓库根目录（合并等操作需在此执行）。</summary>
    public string RepoRoot => _repoRoot;

    /// <summary>创建一个 worktree（目录已存在则快速恢复）。</summary>
    public async Task<(WorktreeInfo? Info, string Error)> CreateAsync(string name, CancellationToken ct = default)
    {
        var (ok, err) = WorktreeValidator.ValidateName(name);
        if (!ok)
        {
            return (null, err);
        }

        var target = Path.Combine(_worktreesDir, WorktreeValidator.NameToDirName(name));
        var branch = WorktreeValidator.NameToBranch(name);

        if (Directory.Exists(target))
        {
            return (new WorktreeInfo(name, target, branch, ReadHead(target)), ""); // 快速恢复
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var (code, _, gitErr) = await RunGitAsync(ct, "worktree", "add", target, "-b", branch).ConfigureAwait(false);
        if (code != 0)
        {
            return (null, $"git worktree add 失败: {gitErr.Trim()}");
        }

        // 初始化环境：复制本地配置 + 链接依赖目录。
        new WorktreeInitializer(_repoRoot, _logger).Initialize(target);

        return (new WorktreeInfo(name, target, branch, ReadHead(target)), "");
    }

    /// <summary>切换工作目录到某 worktree；name 为空则回到主仓库。</summary>
    public async Task<(bool Ok, string Error)> EnterAsync(string name)
    {
        if (!string.IsNullOrEmpty(name))
        {
            var (ok, err) = WorktreeValidator.ValidateName(name);
            if (!ok)
            {
                return (false, err);
            }
            var target = Path.Combine(_worktreesDir, WorktreeValidator.NameToDirName(name));
            if (!Directory.Exists(target))
            {
                return (false, $"工作目录不存在: {target}");
            }
            Directory.SetCurrentDirectory(target);
        }
        else
        {
            Directory.SetCurrentDirectory(_originalCwd);
        }

        Active = name;
        return await Task.FromResult((true, "")).ConfigureAwait(false);
    }

    /// <summary>离开当前 worktree，cwd 切回主仓库；worktree 与分支均保留（非破坏性）。</summary>
    public async Task<(bool Ok, string Error)> ExitAsync()
    {
        if (string.IsNullOrEmpty(Active))
        {
            return (false, "当前不在任何 worktree 中。");
        }
        await EnterAsync("").ConfigureAwait(false); // 切回主仓库，保留 worktree
        return (true, "");
    }

    /// <summary>删除 worktree 及其分支；有未提交修改时需 <paramref name="force"/>。</summary>
    public async Task<(bool Ok, string Error)> RemoveAsync(string name, bool force = false, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(name))
        {
            return (false, "未指定工作目录名");
        }
        var target = Path.Combine(_worktreesDir, WorktreeValidator.NameToDirName(name));
        if (!Directory.Exists(target))
        {
            return (false, $"工作目录不存在: {target}");
        }

        if (!force && await HasChangesAsync(target, ct).ConfigureAwait(false))
        {
            return (false, $"工作目录 '{name}' 有未提交的修改。先提交/合并，或用 force 强制删除（改动会丢失）。");
        }

        if (Active == name)
        {
            await EnterAsync("").ConfigureAwait(false); // 删除当前 worktree 前先切回主仓库
        }
        await RunGitAsync(ct, "worktree", "remove", target, "--force").ConfigureAwait(false);
        await RunGitAsync(ct, "branch", "-D", WorktreeValidator.NameToBranch(name)).ConfigureAwait(false);
        return (true, "");
    }

    /// <summary>列出本管理器创建的 worktree。</summary>
    public async Task<IReadOnlyList<WorktreeInfo>> ListAsync(CancellationToken ct = default)
    {
        var (code, output, _) = await RunGitAsync(ct, "worktree", "list", "--porcelain").ConfigureAwait(false);
        if (code != 0)
        {
            return [];
        }

        var results = new List<WorktreeInfo>();
        foreach (var block in output.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var info = ParsePorcelain(block);
            if (info is not null && info.Path.Replace('\\', '/').Contains("/.vesticode/worktrees/"))
            {
                results.Add(info);
            }
        }
        return results;
    }

    /// <summary>worktree 闲置超过该时长且无修改才视为“过期”，可被后台清理。</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(12);

    /// <summary>清理过期（长时间闲置且无修改）的 worktree。新创建/在用的不会被动。</summary>
    public async Task<IReadOnlyList<string>> RemoveStaleAsync(CancellationToken ct = default)
    {
        var removed = new List<string>();
        foreach (var wt in await ListAsync(ct).ConfigureAwait(false))
        {
            if (wt.Name == Active || !wt.Branch.StartsWith("vesticode/", StringComparison.Ordinal))
            {
                continue;
            }
            // 仅清理“过期”的：闲置时间不足阈值的（含刚创建的）一律保留。
            if (!IsStale(wt.Path))
            {
                continue;
            }
            if (await HasChangesAsync(wt.Path, ct).ConfigureAwait(false))
            {
                continue; // 失败关闭：不删有修改的
            }
            await RunGitAsync(ct, "worktree", "remove", wt.Path, "--force").ConfigureAwait(false);
            await RunGitAsync(ct, "branch", "-D", wt.Branch).ConfigureAwait(false);
            removed.Add(wt.Name);
        }
        return removed;
    }

    /// <summary>worktree 是否已闲置超过 <see cref="StaleAfter"/>（按目录最近写入时间判断）。</summary>
    private static bool IsStale(string path)
    {
        try
        {
            return DateTime.UtcNow - Directory.GetLastWriteTimeUtc(path) > StaleAfter;
        }
        catch (Exception)
        {
            return false; // 读不到时间就保守保留
        }
    }

    // -- 内部 ------------------------------------------------------------------

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                File.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    private static string ReadHead(string path)
    {
        try
        {
            var gitMarker = Path.Combine(path, ".git");
            string headFile;
            if (File.Exists(gitMarker)) // worktree 的 .git 是指向主仓库的文件
            {
                var gitDir = File.ReadAllText(gitMarker).Trim().Split(": ")[^1];
                headFile = Path.Combine(gitDir, "HEAD");
            }
            else
            {
                headFile = Path.Combine(gitMarker, "HEAD");
            }
            return File.Exists(headFile) ? File.ReadAllText(headFile).Trim() : "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private async Task<bool> HasChangesAsync(string path, CancellationToken ct)
    {
        var (code, output, _) = await RunGitAsync(ct, "-C", path, "status", "--porcelain").ConfigureAwait(false);
        return code == 0 && output.Trim().Length > 0;
    }

    private async Task<(int Code, string Stdout, string Stderr)> RunGitAsync(CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _repoRoot,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return (process.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "git 命令失败: {Args}", string.Join(' ', args));
            return (-1, "", ex.Message);
        }
    }

    private static WorktreeInfo? ParsePorcelain(string block)
    {
        string? path = null, head = null, branch = null;
        foreach (var line in block.Split('\n'))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                path = line[9..];
            }
            else if (line.StartsWith("HEAD ", StringComparison.Ordinal))
            {
                head = line[5..];
            }
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                branch = line[7..].Replace("refs/heads/", "");
            }
        }
        if (path is null)
        {
            return null;
        }
        var name = new DirectoryInfo(path).Name.Replace('-', '/');
        return new WorktreeInfo(name, path, branch ?? "", head ?? "");
    }
}
