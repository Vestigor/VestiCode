using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace VestiCode.Core.Mcp;

/// <summary>Stdio 传输：启动子进程，通过 stdin/stdout 收发 JSON-RPC（每行一条）。</summary>
public sealed class StdioTransport(
    string command,
    IReadOnlyList<string> args,
    IReadOnlyDictionary<string, string> env,
    double timeoutSeconds) : IMcpTransport
{
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonRpc.Response>> _pending = new();
    private readonly Lock _idGate = new();
    private Process? _process;
    private Task? _readLoop;
    private int _nextId = 1;

    public bool IsConnected => _process is { HasExited: false };

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo(command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Directory.GetCurrentDirectory(), // 子进程以当前项目目录为 cwd（相对路径/"." 才可预期）
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        foreach (var (k, v) in env)
        {
            psi.Environment[k] = v;
        }

        _process = Process.Start(psi) ?? throw new InvalidOperationException($"无法启动 MCP server: {command}");
        _readLoop = Task.Run(() => ReadLoopAsync(_process));
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync()
    {
        if (_process is not null)
        {
            try
            {
                _process.StandardInput.Close();
                if (!_process.WaitForExit(3000))
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
                // 忽略关闭异常
            }
            _process.Dispose();
            _process = null;
        }
        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 读循环停止
            }
        }
        foreach (var tcs in _pending.Values)
        {
            tcs.TrySetException(new IOException("传输已断开"));
        }
        _pending.Clear();
    }

    public async Task<JsonRpc.Response> SendRequestAsync(string method, JsonObject? @params, CancellationToken cancellationToken = default)
    {
        if (_process is null)
        {
            throw new InvalidOperationException("传输未连接");
        }

        int id;
        lock (_idGate)
        {
            id = _nextId++;
        }
        var tcs = new TaskCompletionSource<JsonRpc.Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        await _process.StandardInput.WriteLineAsync(JsonRpc.Encode(new JsonRpc.Request(method, @params, id)).AsMemory(), cancellationToken).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        await using var reg = linked.Token.Register(() => tcs.TrySetCanceled());
        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async Task SendNotificationAsync(string method, JsonObject? @params = null, CancellationToken cancellationToken = default)
    {
        if (_process is null)
        {
            return;
        }
        await _process.StandardInput.WriteLineAsync(JsonRpc.Encode(new JsonRpc.Notification(method, @params)).AsMemory(), cancellationToken).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (JsonRpc.DecodeResponse(line.Trim()) is { } resp && _pending.TryGetValue(resp.Id, out var tcs))
                {
                    tcs.TrySetResult(resp);
                }
            }
        }
        catch (Exception)
        {
            // EOF / 进程退出
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);
}
