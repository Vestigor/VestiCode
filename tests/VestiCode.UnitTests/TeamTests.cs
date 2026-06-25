using VestiCode.Core.Teams;
using VestiCode.Core.Tools;

namespace VestiCode.UnitTests;

public sealed class TeamTests
{
    [Fact]
    public void SharedTaskList_ReadyTasks_RespectsDependencies()
    {
        var dir = Directory.CreateTempSubdirectory("vesticode_team_");
        try
        {
            var tasks = new SharedTaskList(dir.FullName);
            var a = tasks.Create("A");
            var b = tasks.Create("B", dependsOn: [a.Id]);

            // 初始：只有 A 就绪（B 依赖 A）。
            Assert.Equal([a.Id], tasks.ReadyTasks().Select(t => t.Id));

            tasks.Complete(a.Id);
            // A 完成后 B 就绪。
            Assert.Equal([b.Id], tasks.ReadyTasks().Select(t => t.Id));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void SharedTaskList_PersistsAcrossInstances()
    {
        var dir = Directory.CreateTempSubdirectory("vesticode_team_");
        try
        {
            var t1 = new SharedTaskList(dir.FullName);
            var task = t1.Create("持久化任务");
            t1.Complete(task.Id, "done");

            var t2 = new SharedTaskList(dir.FullName);
            var loaded = t2.Get(task.Id);
            Assert.NotNull(loaded);
            Assert.Equal(TeamTaskStatus.Completed, loaded!.Status);
            Assert.Equal("done", loaded.Result);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Mailbox_PointToPoint_AndReadNew()
    {
        var dir = Directory.CreateTempSubdirectory("vesticode_team_");
        try
        {
            var bob = new Mailbox(dir.FullName, "bob");
            bob.Send(new TeamMessage { From = "alice", To = "bob", Content = "hi" });
            bob.Send(new TeamMessage { From = "alice", To = "bob", Content = "again" });

            var msgs = bob.ReadNew();
            Assert.Equal(2, msgs.Count);
            Assert.Equal("hi", msgs[0].Content);

            // 从第一条之后读 → 只剩第二条。
            var after = bob.ReadNew(msgs[0].Id);
            Assert.Single(after);
            Assert.Equal("again", after[0].Content);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void DispatchScheduler_DoubleLock_StripsTools()
    {
        var scheduler = new DispatchScheduler();
        var tools = new List<ToolDefinition>
        {
            new("write_file", "", []),
            new("team_create_task", "", []),
        };

        // 单锁不生效。
        scheduler.SetLock1(true);
        Assert.False(scheduler.IsActive);
        Assert.Equal(2, scheduler.FilterTools(tools).Count);

        // 双锁生效 → 剥夺 write_file，保留 team_*。
        scheduler.SetLock2(true);
        Assert.True(scheduler.IsActive);
        var filtered = scheduler.FilterTools(tools);
        Assert.Single(filtered);
        Assert.Equal("team_create_task", filtered[0].Name);
    }
}
