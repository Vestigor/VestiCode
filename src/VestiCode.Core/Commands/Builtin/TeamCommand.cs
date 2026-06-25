using VestiCode.Core.Teams;

namespace VestiCode.Core.Commands.Builtin;

/// <summary>/team — 列出团队 / 运行团队完成目标。</summary>
public sealed class TeamCommand(TeamManager manager) : ICommand
{
    public string Name => "team";
    public string Description => "多 Agent 团队协作";
    public string Usage => "/team <list | run <name> <goal>>   例：/team run buildhello 实现登录功能";

    public async Task<CommandResult> ExecuteAsync(IReadOnlyList<string> args, IUiControl ui)
    {
        var sub = args.Count > 0 ? args[0].ToLowerInvariant() : "list";

        if (sub == "list")
        {
            var teams = manager.ListTeams();
            return CommandResult.Show(teams.Count == 0
                ? "暂无团队定义（放在 ~/.vesticode/teams/<name>.json）。"
                : "可用团队：\n" + string.Join("\n", teams.Select(t => $"  - {t}")));
        }

        if (sub == "run")
        {
            if (args.Count < 3)
            {
                return CommandResult.Show("用法: /team run <name> <goal>");
            }
            var name = args[1];
            var goal = string.Join(' ', args.Skip(2));
            ui.WriteNotice($"团队 '{name}' 启动…");
            var result = await manager.RunTeamAsync(name, goal, ui.WriteNotice).ConfigureAwait(false);
            return CommandResult.Show(result);
        }

        return CommandResult.Show($"未知子命令: {sub}。可用: list / run");
    }
}
