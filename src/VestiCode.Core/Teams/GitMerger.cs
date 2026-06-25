using System.Diagnostics;
using System.Text;
using VestiCode.Core.Llm;

namespace VestiCode.Core.Teams;

/// <summary>Git 合并器：增量合并 worktree 分支回主干，冲突时调用 LLM 裁决，失败回滚。</summary>
public sealed class GitMerger(ILlmProvider provider, string repoRoot)
{
    public async Task<(bool Ok, string Message)> MergeAsync(string sourceBranch, string targetBranch = "main", CancellationToken ct = default)
    {
        var (code, _, err) = await GitAsync(ct, "checkout", targetBranch).ConfigureAwait(false);
        if (code != 0)
        {
            return (false, $"checkout {targetBranch} 失败: {err.Trim()}");
        }

        var (mergeCode, mergeOut, mergeErr) = await GitAsync(ct, "merge", sourceBranch, "--no-commit", "--no-ff").ConfigureAwait(false);

        // 主工作区残留的"未跟踪文件"会挡住合并（git 拒绝覆盖）。这些文件的权威版本就在
        // 待合入分支里、本就要被覆盖——删掉挡路的未跟踪文件后重试一次。
        if (mergeCode != 0 && mergeErr.Contains("untracked working tree files would be overwritten", StringComparison.Ordinal))
        {
            await GitAsync(ct, "merge", "--abort").ConfigureAwait(false); // 清理可能的半成品（无则忽略）
            foreach (var rel in ParseUntrackedBlockers(mergeErr))
            {
                try { File.Delete(Path.Combine(repoRoot, rel)); } catch (Exception) { /* 尽力而为 */ }
            }
            (mergeCode, mergeOut, mergeErr) = await GitAsync(ct, "merge", sourceBranch, "--no-commit", "--no-ff").ConfigureAwait(false);
        }

        code = mergeCode;
        if (code == 0)
        {
            await GitAsync(ct, "commit", "-m", $"Merge {sourceBranch}").ConfigureAwait(false);
            return (true, $"已合并 {sourceBranch}（无冲突）");
        }

        var conflicts = await ConflictFilesAsync(ct).ConfigureAwait(false);
        if (conflicts.Count == 0)
        {
            // 合并失败但无未合并文件 → 非冲突原因（如 unrelated histories / 分支无提交 / 工作区脏）。
            await GitAsync(ct, "merge", "--abort").ConfigureAwait(false);
            var detail = $"{mergeErr} {mergeOut}".Trim();
            return (false, $"合并 {sourceBranch} 失败（非冲突，已回滚）：{(detail.Length > 0 ? detail : "git merge 返回非零但无报错")}");
        }

        if (await ResolveConflictsAsync(conflicts, ct).ConfigureAwait(false))
        {
            await GitAsync(ct, "add", ".").ConfigureAwait(false);
            await GitAsync(ct, "commit", "-m", $"Merge {sourceBranch} (LLM resolved)").ConfigureAwait(false);
            return (true, $"已合并 {sourceBranch}（LLM 解决 {conflicts.Count} 个冲突）");
        }

        await GitAsync(ct, "merge", "--abort").ConfigureAwait(false);
        return (false, $"合并 {sourceBranch} 失败：LLM 无法解决冲突，已回滚");
    }

    /// <summary>从 git 的"untracked would be overwritten"报错里解析出挡路的文件路径（以制表符缩进列出）。</summary>
    private static IEnumerable<string> ParseUntrackedBlockers(string stderr)
    {
        foreach (var line in stderr.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith('\t') || line.StartsWith("    ", StringComparison.Ordinal))
            {
                var path = line.Trim();
                if (path.Length > 0)
                {
                    yield return path;
                }
            }
        }
    }

    private async Task<List<string>> ConflictFilesAsync(CancellationToken ct)
    {
        var (code, output, _) = await GitAsync(ct, "diff", "--name-only", "--diff-filter=U").ConfigureAwait(false);
        return code == 0
            ? output.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList()
            : [];
    }

    private async Task<bool> ResolveConflictsAsync(List<string> files, CancellationToken ct)
    {
        foreach (var file in files)
        {
            var full = Path.Combine(repoRoot, file);
            string content;
            try
            {
                content = await File.ReadAllTextAsync(full, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return false;
            }

            var prompt = $"以下是 git merge 冲突文件 '{file}'。请输出解决冲突后的完整文件内容，只输出内容、不要解释。\n\n{content}";
            var sb = new StringBuilder();
            await foreach (var item in provider.ChatStreamAsync([ChatMessage.FromUser(prompt)], tools: null, ct).ConfigureAwait(false))
            {
                if (item is TextDelta td)
                {
                    sb.Append(td.Text);
                }
            }

            var resolved = sb.ToString().Trim();
            if (resolved.Length == 0)
            {
                return false;
            }
            await File.WriteAllTextAsync(full, resolved, ct).ConfigureAwait(false);
        }
        return true;
    }

    private async Task<(int Code, string Stdout, string Stderr)> GitAsync(CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = repoRoot,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return (process.ExitCode, stdout, stderr);
    }
}
