using System.Text;
using VestiCode.Core.Llm;
using VestiCode.Core.Teams;
using VestiCode.Core.Worktree;

namespace VestiCode.Core.Commands.Builtin;

/// <summary>/worktree — 管理 Git worktree 隔离环境。</summary>
public sealed class WorktreeCommand(GitWorktreeManager manager, ILlmProvider provider) : ICommand
{
    public string Name => "worktree";
    public IReadOnlyList<string> Aliases => ["wt"];
    public string Description => "Git worktree 隔离工作区";
    public string Usage =>
        "/worktree <status | list | create <name> | enter <name> | merge <name> | exit | remove <name> [force]>";

    public async Task<CommandResult> ExecuteAsync(IReadOnlyList<string> args, IUiControl ui)
    {
        var sub = args.Count > 0 ? args[0].ToLowerInvariant() : "status";

        switch (sub)
        {
            case "status":
                if (string.IsNullOrEmpty(manager.Active))
                {
                    return CommandResult.Show("当前在主仓库（未进入任何 worktree）。");
                }
                var current = (await manager.ListAsync().ConfigureAwait(false))
                    .FirstOrDefault(w => w.Name == manager.Active);
                return CommandResult.Show(current is null
                    ? $"当前 worktree: {manager.Active}"
                    : $"当前 worktree: {current.Name}  [{current.Branch}]\n  {current.Path}");

            case "list":
                var list = await manager.ListAsync().ConfigureAwait(false);
                if (list.Count == 0)
                {
                    return CommandResult.Show("当前没有 worktree。");
                }
                var sb = new StringBuilder("Worktree：\n");
                foreach (var wt in list)
                {
                    var mark = wt.Name == manager.Active ? "●" : "○";
                    sb.Append($"  {mark} {wt.Name}  [{wt.Branch}]  {wt.Path}\n");
                }
                return CommandResult.Show(sb.ToString().TrimEnd());

            case "create":
                if (args.Count < 2)
                {
                    return CommandResult.Show("用法: /worktree create <name>");
                }
                var (info, err) = await manager.CreateAsync(args[1]).ConfigureAwait(false);
                return CommandResult.Show(info is not null
                    ? $"已创建 worktree '{info.Name}'（分支 {info.Branch}）于 {info.Path}"
                    : $"创建失败: {err}");

            case "enter":
                if (args.Count < 2)
                {
                    return CommandResult.Show("用法: /worktree enter <name>");
                }
                var (okEnter, errEnter) = await manager.EnterAsync(args[1]).ConfigureAwait(false);
                return CommandResult.Show(okEnter ? $"已进入 worktree '{args[1]}'。" : $"进入失败: {errEnter}");

            case "exit":
                var (okExit, errExit) = await manager.ExitAsync().ConfigureAwait(false);
                return CommandResult.Show(okExit ? "已离开 worktree，回到主仓库（worktree 与分支已保留）。" : errExit);

            case "merge":
                if (args.Count < 2)
                {
                    return CommandResult.Show("用法: /worktree merge <name>（把 vesticode/<name> 合并回 main）");
                }
                var branch = WorktreeValidator.NameToBranch(args[1]);
                var merger = new GitMerger(provider, manager.RepoRoot);
                var (okMerge, mergeMsg) = await merger.MergeAsync(branch).ConfigureAwait(false);
                return CommandResult.Show(okMerge
                    ? $"{mergeMsg}\n（worktree 仍保留；确认无误后可用 /worktree remove {args[1]} 清理）"
                    : mergeMsg);

            case "remove":
            case "rm":
                if (args.Count < 2)
                {
                    return CommandResult.Show("用法: /worktree remove <name> [force]");
                }
                var force = args.Count > 2 && args[2].Equals("force", StringComparison.OrdinalIgnoreCase);
                var (okRemove, errRemove) = await manager.RemoveAsync(args[1], force).ConfigureAwait(false);
                return CommandResult.Show(okRemove ? $"已删除 worktree '{args[1]}' 及其分支。" : $"删除失败: {errRemove}");

            default:
                return CommandResult.Show($"未知子命令: {sub}。可用: status / list / create / enter / exit / merge / remove");
        }
    }
}
