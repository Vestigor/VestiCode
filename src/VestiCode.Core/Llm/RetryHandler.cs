using System.Net;
using Microsoft.Extensions.Logging;

namespace VestiCode.Core.Llm;

/// <summary>
/// 生产硬化：对瞬时失败（429 / 5xx / 网络异常）做指数退避重试的委托处理器。
/// 装在 LLM 的命名 HttpClient 上，对所有 Provider 自动生效。
/// </summary>
public sealed class RetryHandler(ILogger<RetryHandler> logger) : DelegatingHandler
{
    private const int MaxAttempts = 3;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (attempt >= MaxAttempts || !IsTransient(response.StatusCode))
                {
                    return response;
                }
                response.Dispose(); // 将重试，丢弃本次响应
                logger.LogWarning("LLM 请求瞬时失败 {Status}，第 {Attempt}/{Max} 次重试", response.StatusCode, attempt, MaxAttempts);
            }
            catch (HttpRequestException ex) when (attempt < MaxAttempts)
            {
                logger.LogWarning(ex, "LLM 请求网络异常，第 {Attempt}/{Max} 次重试", attempt, MaxAttempts);
            }

            // 指数退避：~0.5s, 1s, 2s（+ 抖动）。
            var delay = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1) + Random.Shared.Next(0, 250));
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status == HttpStatusCode.TooManyRequests || (int)status >= 500;
}
