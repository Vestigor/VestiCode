using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace VestiCode.Core.Tools;

/// <summary>
/// 工具执行器：带超时与异常隔离地运行工具。
/// 超时或意外异常都转换为结构化失败 <see cref="ToolResult"/>，
/// 让模型可以据此调整，而不是让整个 Agent 崩溃。
/// </summary>
public sealed class ToolExecutor(ILogger<ToolExecutor> logger)
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>执行 <paramref name="tool"/>，超时由 <paramref name="timeout"/> 控制（默认 30s）。</summary>
    public async Task<ToolResult> ExecuteAsync(
        ITool tool,
        JsonObject arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;

        // 把外部取消令牌与超时合并：任一触发都会取消工具执行。
        using var timeoutCts = new CancellationTokenSource(effectiveTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        try
        {
            return await tool.ExecuteAsync(arguments, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested
                                                 && !cancellationToken.IsCancellationRequested)
        {
            // 是超时导致的取消（而非用户主动取消）。
            logger.LogWarning("工具 {Tool} 执行超时（{Timeout}s）", tool.Name, effectiveTimeout.TotalSeconds);
            return ToolResult.Fail($"工具 '{tool.Name}' 执行超时（{effectiveTimeout.TotalSeconds}s）");
        }
        catch (OperationCanceledException)
        {
            // 用户主动取消：向上传播，由 Agent 循环统一处理。
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "工具 {Tool} 执行异常", tool.Name);
            return ToolResult.Fail($"工具 '{tool.Name}' 执行异常: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
