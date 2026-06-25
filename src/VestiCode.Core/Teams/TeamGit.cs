using System.Diagnostics;

namespace VestiCode.Core.Teams;

/// <summary>在指定目录运行 git 命令的轻量助手（团队 worktree 创建/提交/清理用）。</summary>
internal static class TeamGit
{
    public static async Task<(int Code, string Stdout, string Stderr)> RunAsync(
        string cwd, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = cwd,
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
