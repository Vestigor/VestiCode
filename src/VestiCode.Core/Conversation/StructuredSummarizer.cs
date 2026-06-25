using System.Text;
using VestiCode.Core.Llm;

namespace VestiCode.Core.Conversation;

/// <summary>层2 摘要结果。</summary>
public sealed record SummaryResult(
    string SummaryText = "",
    int MessagesCompressed = 0,
    int TokensSaved = 0,
    bool BoundaryAdded = false);

/// <summary>
/// 层2 token 管理：当对话接近上下文窗口时，用 LLM 生成 9 段结构化摘要替代早期消息，
/// 保留最近 <see cref="KeepRecent"/> 条原文。带熔断器防止反复失败。
/// </summary>
public sealed class StructuredSummarizer
{
    private const double CharsPerToken = 3.5;
    private const double WarnFraction = 0.7;     // 70%：仅提醒
    private const double CompressFraction = 0.9; // 90%：触发自动压缩
    private const int KeepRecent = 4;
    private const int DefaultWindow = 128_000;
    private const int PerMessageCap = 3_000;

    // 已知模型的上下文窗口（按前缀匹配）。
    private static readonly (string Prefix, int Window)[] ModelWindows =
    [
        ("claude-", 200_000),
        ("gpt-4.1", 1_000_000),
        ("gpt-4", 128_000),
        ("gpt-3.5", 16_385),
        ("o1", 200_000), ("o3", 200_000), ("o4", 200_000),
        ("deepseek", 64_000),
    ];

    private readonly ILlmProvider _provider;
    private readonly CircuitBreaker _breaker = new();

    public StructuredSummarizer(ILlmProvider provider, string model)
    {
        _provider = provider;
        ContextWindow = ResolveWindow(model);
        WarnThreshold = (int)(ContextWindow * WarnFraction);
        CompressThreshold = (int)(ContextWindow * CompressFraction);
    }

    public int ContextWindow { get; }

    /// <summary>70% 窗口：仅发出接近上限的警告。</summary>
    public int WarnThreshold { get; }

    /// <summary>90% 窗口：触发昂贵的结构化摘要压缩。</summary>
    public int CompressThreshold { get; }

    public bool CircuitOpen => _breaker.IsOpen;

    public void ResetCircuit() => _breaker.Reset();

    /// <summary>token 估算是否已越过 70% 警告线。</summary>
    public bool NeedsWarning(IReadOnlyList<ChatMessage> messages) =>
        EstimateTokens(messages) >= WarnThreshold;

    /// <summary>token 估算是否已越过 90% 自动压缩线。</summary>
    public bool NeedsCompression(IReadOnlyList<ChatMessage> messages) =>
        EstimateTokens(messages) >= CompressThreshold;

    /// <summary>生成结构化摘要。失败返回原消息 + 零结果，并记一次熔断失败。</summary>
    public async Task<(List<ChatMessage> Messages, SummaryResult Result)> SummarizeAsync(
        IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        if (messages.Count < KeepRecent + 4)
        {
            return (messages.ToList(), new SummaryResult());
        }

        var split = messages.Count - KeepRecent;
        var old = messages.Take(split).ToList();
        var recent = messages.Skip(split).ToList();

        var input = SummaryPrompt + "\n\n---\n对话内容:\n" + FormatForSummary(old);

        string summaryText;
        try
        {
            var sb = new StringBuilder();
            await foreach (var item in _provider
                .ChatStreamAsync([ChatMessage.FromUser(input)], tools: null, cancellationToken)
                .ConfigureAwait(false))
            {
                switch (item)
                {
                    case TextDelta td:
                        sb.Append(td.Text);
                        break;
                    case StreamError:
                        throw new InvalidOperationException("摘要请求失败");
                }
            }
            summaryText = StripDraft(sb.ToString());
            if (string.IsNullOrWhiteSpace(summaryText))
            {
                throw new InvalidOperationException("摘要生成返回空内容");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _breaker.RecordFailure();
            return (messages.ToList(), new SummaryResult());
        }

        _breaker.RecordSuccess();

        var oldChars = old.Sum(m => m.Text?.Length ?? 0);
        var savedTokens = (int)(oldChars / CharsPerToken) - (int)(summaryText.Length / CharsPerToken);

        var newMessages = new List<ChatMessage>
        {
            ChatMessage.FromSystem($"[结构化摘要]\n{summaryText}"),
            ChatMessage.FromSystem(BoundaryMessage),
        };
        newMessages.AddRange(recent);

        return (newMessages, new SummaryResult(summaryText, old.Count, Math.Max(0, savedTokens), true));
    }

    // -- 内部 ------------------------------------------------------------------

    public static int EstimateTokens(IReadOnlyList<ChatMessage> messages)
    {
        var chars = 0;
        foreach (var m in messages)
        {
            chars += m.Text?.Length ?? 0;
            foreach (var c in m.ToolCalls)
            {
                chars += c.Arguments.ToJsonString().Length;
            }
        }
        return (int)(chars / CharsPerToken);
    }

    private static int ResolveWindow(string model)
    {
        foreach (var (prefix, window) in ModelWindows)
        {
            if (model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return window;
            }
        }
        return DefaultWindow;
    }

    /// <summary>去掉 ```draft ... ``` 草稿块，只保留其后的正式摘要。</summary>
    private static string StripDraft(string text)
    {
        var first = text.IndexOf("```", StringComparison.Ordinal);
        if (first == -1)
        {
            return text.Trim();
        }
        var second = text.IndexOf("```", first + 3, StringComparison.Ordinal);
        return second == -1 ? text.Trim() : text[(second + 3)..].Trim();
    }

    private static string FormatForSummary(IReadOnlyList<ChatMessage> messages)
    {
        var parts = messages.Select(m =>
        {
            var text = m.Text ?? "";
            if (text.Length > PerMessageCap)
            {
                text = text[..PerMessageCap];
            }
            return $"[{m.Role}]: {text}";
        });
        return string.Join("\n\n", parts);
    }

    private const string SummaryPrompt =
        """
        你是一个对话摘要生成器。**只生成摘要，不要调用任何工具。**

        请分析以下对话，按指定结构生成摘要。每个部分用 ## 标题分隔：

        ## 主要请求
        用户的核心需求——他们想完成什么

        ## 关键概念
        涉及的技术栈、框架、API、库

        ## 文件与代码
        已检查或修改的文件、关键代码片段及其位置

        ## 错误与修复
        遇到的错误信息和修复方式

        ## 解决过程
        问题解决的步骤顺序和时间线

        ## 用户原话
        用户的关键原话（用 > 引用，逐字保留，不要改写）

        ## 待办事项
        尚未完成的任务

        ## 当前工作
        当前正在进行的具体工作

        ## 下一步
        建议的下一步操作

        ---

        先将你的分析写成草稿，用 ```draft ... ``` 包裹。草稿写完后再输出正式摘要。

        **再次强调：不要调用任何工具，只输出摘要文本。**
        """;

    private const string BoundaryMessage =
        "[对话上下文已压缩] 上方的结构化摘要替代了早期的详细对话。" +
        "如果你需要某个文件的完整内容或某段具体代码，请使用 read_file 或 grep 重新读取，不要根据摘要脑补不存在的细节。";
}
