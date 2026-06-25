using System.Text;

namespace VestiCode.Core.Commands.Builtin;

/// <summary>/session — 列出 / 加载 / 新建 / 删除会话。</summary>
public sealed class SessionCommand : ICommand
{
    public string Name => "session";
    public IReadOnlyList<string> Aliases => ["sessions"];
    public string Description => "管理会话";
    public string Usage => "/session <list | load <id> | new | delete <id>>";

    public Task<CommandResult> ExecuteAsync(IReadOnlyList<string> args, IUiControl ui)
    {
        var sub = args.Count > 0 ? args[0].ToLowerInvariant() : "list";

        switch (sub)
        {
            case "list":
                return Task.FromResult(CommandResult.Show(FormatList(ui)));

            case "new":
                return Task.FromResult(CommandResult.Show(ui.NewSession()));

            case "load":
                return args.Count < 2
                    ? Task.FromResult(CommandResult.Show("用法: /session load <id>"))
                    : Task.FromResult(CommandResult.Show(ui.LoadSession(args[1])));

            case "delete" or "rm":
                return args.Count < 2
                    ? Task.FromResult(CommandResult.Show("用法: /session delete <id>"))
                    : Task.FromResult(CommandResult.Show(ui.DeleteSession(args[1])));

            default:
                return Task.FromResult(CommandResult.Show($"未知子命令: {sub}。可用: list / load / new / delete"));
        }
    }

    private static string FormatList(IUiControl ui)
    {
        var sessions = ui.GetSessionList();
        if (sessions.Count == 0)
        {
            return "暂无已保存的会话。";
        }
        var sb = new StringBuilder("已保存的会话：\n");
        foreach (var s in sessions.Take(20))
        {
            sb.Append($"  {s.Id}  [{s.MessageCount} 条] {s.LastActiveAt.LocalDateTime:MM-dd HH:mm}  {s.Title}\n");
        }
        sb.Append("\n用 /session load <id> 恢复某个会话。");
        return sb.ToString().TrimEnd();
    }
}
