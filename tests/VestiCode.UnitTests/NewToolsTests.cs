using System.Text.Json.Nodes;
using VestiCode.Core.Tools.Builtin;

namespace VestiCode.UnitTests;

public sealed class NewToolsTests
{
    [Fact]
    public async Task ReadFile_AddsLineNumbers_AndRespectsOffsetLimit()
    {
        var dir = Directory.CreateTempSubdirectory("vc_read_");
        var prev = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(dir.FullName);
        try
        {
            await File.WriteAllTextAsync("f.txt", "a\nb\nc\nd\ne");
            var tool = new ReadFileTool();

            var full = await tool.ExecuteAsync(new JsonObject { ["path"] = "f.txt" });
            Assert.Contains("     1\ta", full.Content);
            Assert.Contains("     5\te", full.Content);

            // offset=2, limit=2 → 只含第 2、3 行。
            var slice = await tool.ExecuteAsync(new JsonObject { ["path"] = "f.txt", ["offset"] = 2, ["limit"] = 2 });
            Assert.Contains("     2\tb", slice.Content);
            Assert.Contains("     3\tc", slice.Content);
            Assert.DoesNotContain("\ta", slice.Content);
            Assert.Contains("还有 2 行", slice.Content);
        }
        finally
        {
            Directory.SetCurrentDirectory(prev);
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PersistentShell_KeepsCwdAcrossCalls()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // 用例针对 POSIX shell
        }
        var dir = Directory.CreateTempSubdirectory("vc_shell_");
        var prev = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(dir.FullName);
        try
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, "sub"));
            using var shell = new PersistentShell();

            await shell.ExecuteAsync("cd sub", TimeSpan.FromSeconds(10));
            await shell.ExecuteAsync("export FOO=bar", TimeSpan.FromSeconds(10));

            var (code, output, timedOut) = await shell.ExecuteAsync("pwd; echo $FOO", TimeSpan.FromSeconds(10));
            Assert.False(timedOut);
            Assert.Equal(0, code);
            Assert.Contains("/sub", output);   // cd 状态保持
            Assert.Contains("bar", output);     // 环境变量保持
        }
        finally
        {
            Directory.SetCurrentDirectory(prev);
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PersistentShell_NonZeroExitCode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var shell = new PersistentShell();
        var (code, _, timedOut) = await shell.ExecuteAsync("exit 0; false", TimeSpan.FromSeconds(10));
        // 注意：每条命令在同一 shell；用一个明确失败的命令验证退出码捕获。
        var (code2, _, _) = await shell.ExecuteAsync("false", TimeSpan.FromSeconds(10));
        Assert.False(timedOut);
        Assert.NotEqual(0, code2);
    }
}
