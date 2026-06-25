using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace VestiCode.Core.Tools.Builtin;

/// <summary>
/// 持久 shell 会话：用一个长生命周期的 shell 进程执行命令，
/// 因此 <c>cd</c>、环境变量、shell 状态在多次调用间保持（对应 Claude Code 的 Bash 持久会话）。
/// 命令完成通过哨兵行 + 退出码检测；并发调用用信号量串行化（shell 本质串行）。
/// </summary>
public sealed class PersistentShell : IDisposable
{
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private readonly string _sentinel = $"__VC_DONE_{Guid.NewGuid():N}__";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string? _workingDir; // 团队成员：根植于其 worktree
    private Process? _process;

    public PersistentShell() { }
    public PersistentShell(string workingDir) => _workingDir = workingDir;

    /// <summary>执行一条命令，返回（退出码, 合并输出, 是否超时）。</summary>
    public async Task<(int ExitCode, string Output, bool TimedOut)> ExecuteAsync(
        string command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureStarted();
            var process = _process!;

            // 写入命令 + 哨兵（带退出码）。
            await process.StandardInput.WriteLineAsync(command).ConfigureAwait(false);
            await process.StandardInput.WriteLineAsync(
                IsWindows ? $"echo {_sentinel}:%errorlevel%" : $"echo {_sentinel}:$?").ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);

            var output = new StringBuilder();
            var exitCode = 0;
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                while (await process.StandardOutput.ReadLineAsync(linked.Token).ConfigureAwait(false) is { } line)
                {
                    var idx = line.IndexOf(_sentinel, StringComparison.Ordinal);
                    if (idx >= 0)
                    {
                        var code = line[(idx + _sentinel.Length + 1)..].Trim();
                        int.TryParse(code, out exitCode);
                        return (exitCode, output.ToString(), false);
                    }
                    output.Append(line).Append('\n');
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                // 超时：会话可能卡住，重启 shell 以保证后续可用。
                Restart();
                return (-1, output.ToString(), true);
            }

            // 读到 EOF（shell 退出）。
            return (exitCode, output.ToString(), false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureStarted()
    {
        if (_process is { HasExited: false })
        {
            return;
        }

        var psi = new ProcessStartInfo(IsWindows ? "cmd.exe" : "/bin/sh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _workingDir ?? Directory.GetCurrentDirectory(),
        };
        _process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 shell 进程");

        // Unix：把后续所有 stderr 合并到 stdout，便于统一按行读取。
        if (!IsWindows)
        {
            _process.StandardInput.WriteLine("exec 2>&1");
            _process.StandardInput.Flush();
        }
        else
        {
            // Windows：把 stderr 异步抽走（避免缓冲区阻塞）。
            _ = _process.StandardError.ReadToEndAsync();
        }
    }

    private void Restart()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 忽略
        }
        _process?.Dispose();
        _process = null;
    }

    public void Dispose()
    {
        Restart();
        _gate.Dispose();
    }
}
