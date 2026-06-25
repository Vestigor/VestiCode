using VestiCode.Core.SubAgents;

namespace VestiCode.UnitTests;

public sealed class SubAgentTests
{
    [Fact]
    public void RoleLoader_LoadsBuiltinRoles()
    {
        var roles = new RoleLoader().LoadAll();
        Assert.Contains("explorer", roles.Keys);
        Assert.Contains("planner", roles.Keys);
        Assert.Contains("general", roles.Keys);
        Assert.Equal(5, roles["explorer"].MaxRounds);
        Assert.Equal(["read_file", "glob", "grep"], roles["explorer"].ToolsAllow);
    }

    [Fact]
    public void ToolFilter_AppliesRoleWhitelist()
    {
        var roles = new RoleLoader().LoadAll();
        var all = new[] { "read_file", "write_file", "edit_file", "run_command", "glob", "grep", "sub_agent" };

        var explorerTools = ToolFilter.Filter(all, roles["explorer"]);
        Assert.Equal(["glob", "grep", "read_file"], explorerTools);
        Assert.DoesNotContain("write_file", explorerTools);
    }

    [Fact]
    public void ToolFilter_AlwaysBlocksSubAgent_PreventsRecursion()
    {
        var all = new[] { "read_file", "sub_agent" };
        // 即使 fork 模式（role=null，全部工具）也不能再调用 sub_agent。
        var tools = ToolFilter.Filter(all, role: null);
        Assert.DoesNotContain("sub_agent", tools);
        Assert.Contains("read_file", tools);
    }

    [Fact]
    public void Manager_TracksTaskLifecycle()
    {
        var manager = new SubAgentManager();
        var task = manager.Create("explorer", "探索代码");
        Assert.Equal(VestiCode.Core.SubAgents.TaskStatus.Queued, task.Status);

        task.Start();
        Assert.Equal(VestiCode.Core.SubAgents.TaskStatus.Running, task.Status);

        task.Complete("报告内容", rounds: 3);
        Assert.Equal(VestiCode.Core.SubAgents.TaskStatus.Completed, task.Status);
        Assert.Equal(3, task.RoundCount);
        Assert.Contains(task, manager.ListTasks());
    }
}
