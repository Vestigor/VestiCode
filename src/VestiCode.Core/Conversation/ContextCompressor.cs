using VestiCode.Core.Llm;

namespace VestiCode.Core.Conversation;

/// <summary>层2 压缩结果。</summary>
public sealed record CompressionResult(
    bool WasCompressed = false,
    int MessagesCompressed = 0,
    int EstimatedTokensSaved = 0,
    bool WarningIssued = false,
    int EstimatedTokens = 0);

/// <summary>
/// 两层 token 管理协调器：
/// 层1 <see cref="ToolResultTruncator"/>（每次请求前轻量截断）；
/// 层2 <see cref="StructuredSummarizer"/>（接近窗口上限时的昂贵 LLM 摘要）。
/// </summary>
public sealed class ContextCompressor
{
    private readonly ToolResultTruncator _truncator = new();
    private readonly StructuredSummarizer _summarizer;
    private bool _warningEmitted;

    public ContextCompressor(string model, ILlmProvider provider) =>
        _summarizer = new StructuredSummarizer(provider, model);

    public int ContextWindow => _summarizer.ContextWindow;

    /// <summary>/compress 手动触发时调用：复位熔断器并清除一次性警告标记，使压缩可重新进行。</summary>
    public void Reset()
    {
        _warningEmitted = false;
        _summarizer.ResetCircuit();
    }

    /// <summary>层1：截断过大的工具结果（廉价、无 LLM 调用）。</summary>
    public List<ChatMessage> Truncate(IReadOnlyList<ChatMessage> messages) =>
        _truncator.ProcessRound(messages).Messages;

    /// <summary>层2：接近窗口上限时生成结构化摘要，原地改写 <paramref name="history"/>。</summary>
    public async Task<CompressionResult> CheckAndCompressAsync(
        ConversationHistory history, CancellationToken cancellationToken = default)
    {
        var messages = history.GetMessages();
        var tokens = StructuredSummarizer.EstimateTokens(messages);

        // 70% 警告：仅一次性提醒，不动历史。
        var warning = false;
        if (!_warningEmitted && _summarizer.NeedsWarning(messages))
        {
            warning = true;
            _warningEmitted = true;
        }

        // 90% 才真正压缩（昂贵的 LLM 摘要）。
        if (_summarizer.CircuitOpen || !_summarizer.NeedsCompression(messages))
        {
            return new CompressionResult(WarningIssued: warning, EstimatedTokens: tokens);
        }

        var (newMessages, summary) = await _summarizer.SummarizeAsync(messages, cancellationToken)
            .ConfigureAwait(false);
        if (summary.MessagesCompressed == 0)
        {
            return new CompressionResult(WarningIssued: warning, EstimatedTokens: tokens); // 失败，熔断器已记一次
        }

        history.ReplaceMessages(newMessages);
        return new CompressionResult(true, summary.MessagesCompressed, summary.TokensSaved, warning, tokens);
    }
}
