using Microsoft.Extensions.Logging;

namespace VestiCode.Core.Worktree;

/// <summary>每 5 分钟后台清理过期、无修改的 worktree。</summary>
public sealed class BackgroundCleaner(GitWorktreeManager manager, ILogger<BackgroundCleaner> logger)
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常停止
            }
        }
        _cts?.Dispose();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var removed = await manager.RemoveStaleAsync(ct).ConfigureAwait(false);
                if (removed.Count > 0)
                {
                    logger.LogInformation("已清理过期 worktree: {Names}", string.Join(", ", removed));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "worktree 清理失败");
            }
        }
    }
}
