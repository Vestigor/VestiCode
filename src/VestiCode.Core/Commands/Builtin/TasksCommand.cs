using VestiCode.Core.SubAgents;

namespace VestiCode.Core.Commands.Builtin;

/// <summary>/tasks — 查看 / 终止子 Agent 任务。</summary>
public sealed class TasksCommand(SubAgentManager manager) : ICommand
{
    public string Name => "tasks";
    public string Description => "查看或终止子 Agent 任务";
    public string Usage => "/tasks <list | kill <id>>";

    public Task<CommandResult> ExecuteAsync(IReadOnlyList<string> args, IUiControl ui)
    {
        if (args.Count > 0 && args[0].Equals("kill", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Count < 2)
            {
                return Task.FromResult(CommandResult.Show("用法: /tasks kill <id>"));
            }
            return Task.FromResult(CommandResult.Show(manager.Kill(args[1])));
        }
        return Task.FromResult(CommandResult.Show(manager.GetStatusSummary()));
    }
}
