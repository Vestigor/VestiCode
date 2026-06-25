using System.Net.Http;
using Microsoft.Extensions.Logging;
using VestiCode.Core.Llm;
using VestiCode.Core.Tools;
using VestiCode.Core.Worktree;

namespace VestiCode.Core.Teams;

/// <summary>团队入口：按团队定义装配 Lead + 成员（各自 git worktree 隔离）并执行一个目标。</summary>
public sealed class TeamManager(
    ILlmProvider provider,
    ToolExecutor executor,
    ILoggerFactory loggerFactory,
    IHttpClientFactory httpClientFactory)
{
    public IReadOnlyList<string> ListTeams() => TeamPersistence.ListTeamDefs();

    /// <summary>运行某个已定义团队，端到端完成目标。<paramref name="progress"/> 上报实时进度。</summary>
    public async Task<string> RunTeamAsync(
        string teamName, string goal, Action<string>? progress = null, CancellationToken ct = default)
    {
        var def = TeamPersistence.LoadTeamDef(teamName);
        if (def is null)
        {
            return $"未找到团队定义: {teamName}（放在 ~/.vesticode/teams/{teamName}.json）";
        }
        if (def.Members.Count == 0)
        {
            return $"团队 '{teamName}' 没有成员。";
        }

        var repoRoot = Directory.GetCurrentDirectory();
        var (gitCode, _, _) = await TeamGit.RunAsync(repoRoot, ct, "rev-parse", "--is-inside-work-tree").ConfigureAwait(false);
        if (gitCode != 0)
        {
            return "团队功能需要在 git 仓库中运行（成员各自在 git worktree 中隔离工作）。当前目录不是 git 仓库。";
        }

        // worktree 需要从 HEAD 拉分支；空仓库（git init 后还没提交）没有 HEAD，会失败。
        var (headCode, _, _) = await TeamGit.RunAsync(repoRoot, ct, "rev-parse", "--verify", "HEAD").ConfigureAwait(false);
        if (headCode != 0)
        {
            return "当前 git 仓库还没有任何提交（无 HEAD），无法创建 worktree。请先提交一次再运行团队：\n  git add -A && git commit -m init";
        }

        var teamDir = TeamPersistence.GetTeamDir(teamName);
        var tasks = new SharedTaskList(teamDir);
        var memberNames = def.Members.Select(m => m.Name).ToList();

        progress?.Invoke($"为 {def.Members.Count} 名成员创建隔离工作区（git worktree）…");

        // 为每个成员创建独立的 git worktree（分支 vesticode/<worktree>），并清理可能的残留。
        var members = new Dictionary<string, TeamMember>(StringComparer.Ordinal);
        foreach (var md in def.Members)
        {
            var workspaceDir = repoRoot;
            if (!string.IsNullOrEmpty(md.Worktree))
            {
                var dirName = WorktreeValidator.NameToDirName(md.Worktree);
                var branch = WorktreeValidator.NameToBranch(md.Worktree);
                workspaceDir = Path.Combine(repoRoot, ".vesticode", "worktrees", dirName);

                await TeamGit.RunAsync(repoRoot, ct, "worktree", "remove", "--force", workspaceDir).ConfigureAwait(false);
                await TeamGit.RunAsync(repoRoot, ct, "worktree", "prune").ConfigureAwait(false);
                await TeamGit.RunAsync(repoRoot, ct, "branch", "-D", branch).ConfigureAwait(false);

                var (code, _, err) = await TeamGit.RunAsync(repoRoot, ct, "worktree", "add", workspaceDir, "-b", branch).ConfigureAwait(false);
                if (code != 0)
                {
                    return $"为成员 {md.Name} 创建 worktree 失败：{err.Trim()}";
                }
            }

            members[md.Name] = new TeamMember(
                md, teamDir, provider, workspaceDir, executor, loggerFactory,
                tasks, memberNames, def.MaxRoundsPerMember, httpClientFactory);
        }

        var merger = new GitMerger(provider, repoRoot);
        var lead = new LeadAgent(def, members, tasks, merger, provider, repoRoot, executor, loggerFactory);
        var result = await lead.ExecuteAsync(goal, progress, ct).ConfigureAwait(false);

        // 合并完成后移除 worktree 目录（分支保留，便于查看；下次运行会重建）。
        foreach (var md in def.Members.Where(m => !string.IsNullOrEmpty(m.Worktree)))
        {
            var dir = Path.Combine(repoRoot, ".vesticode", "worktrees", WorktreeValidator.NameToDirName(md.Worktree));
            await TeamGit.RunAsync(repoRoot, ct, "worktree", "remove", "--force", dir).ConfigureAwait(false);
        }
        await TeamGit.RunAsync(repoRoot, ct, "worktree", "prune").ConfigureAwait(false);

        return result;
    }
}
