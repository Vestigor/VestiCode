using System.Text.RegularExpressions;
using VestiCode.Core.Llm;

namespace VestiCode.Core.Conversation;

/// <summary>层1 截断配置。</summary>
public sealed class TruncateConfig
{
    /// <summary>单条工具结果超过此字符数则写盘留预览。</summary>
    public int PerResultThreshold { get; init; } = 100_000;

    /// <summary>单轮所有工具结果合计超过此字符数则从最大的开始截断。</summary>
    public int TotalRoundThreshold { get; init; } = 500_000;

    /// <summary>保留在对话中的预览字符数。</summary>
    public int PreviewLength { get; init; } = 2_000;

    /// <summary>完整内容落盘目录。</summary>
    public string StorageDir { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vesticode", "tool_results");
}

/// <summary>一次截断的记录。</summary>
public sealed record TruncationInfo(string ToolName, int OriginalChars, string FilePath);

/// <summary>
/// 层1 token 管理：把过大的单条工具结果写盘并替换为预览，并在单轮合计超限时
/// 从最大的结果开始截断。轻量、纯本地，每次请求前运行。
/// </summary>
public sealed class ToolResultTruncator
{
    private readonly TruncateConfig _cfg;

    public ToolResultTruncator(TruncateConfig? config = null)
    {
        _cfg = config ?? new TruncateConfig();
        Directory.CreateDirectory(_cfg.StorageDir);
    }

    /// <summary>处理一轮消息：返回（截断后的消息, 截断记录）。</summary>
    public (List<ChatMessage> Messages, List<TruncationInfo> Infos) ProcessRound(IReadOnlyList<ChatMessage> messages)
    {
        var infos = new List<TruncationInfo>();

        // 统计每条 tool 消息的字符数。
        var toolSizes = new List<(int Index, int Chars)>();
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Role == ChatRole.Tool)
            {
                toolSizes.Add((i, messages[i].Text?.Length ?? 0));
            }
        }

        var total = toolSizes.Sum(t => t.Chars);
        if (total <= _cfg.TotalRoundThreshold)
        {
            // 仅按单条阈值截断。
            return (TruncateIndividual(messages, infos), infos);
        }

        // 从最大的开始截断，直到单轮合计回落到阈值内。
        var toTruncate = new HashSet<int>();
        foreach (var (index, chars) in toolSizes.OrderByDescending(t => t.Chars))
        {
            toTruncate.Add(index);
            total -= Math.Max(0, chars - _cfg.PerResultThreshold);
            if (total <= _cfg.TotalRoundThreshold)
            {
                break;
            }
        }

        var result = new List<ChatMessage>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            if (toTruncate.Contains(i) && messages[i].Role == ChatRole.Tool)
            {
                var (truncated, path) = TruncateToolMessage(messages[i]);
                result.Add(truncated);
                infos.Add(new TruncationInfo(messages[i].ToolName ?? "unknown", messages[i].Text?.Length ?? 0, path));
            }
            else
            {
                result.Add(messages[i]);
            }
        }
        return (result, infos);
    }

    private List<ChatMessage> TruncateIndividual(IReadOnlyList<ChatMessage> messages, List<TruncationInfo> infos)
    {
        var result = new List<ChatMessage>(messages.Count);
        foreach (var msg in messages)
        {
            if (msg.Role == ChatRole.Tool && (msg.Text?.Length ?? 0) > _cfg.PerResultThreshold)
            {
                var (truncated, path) = TruncateToolMessage(msg);
                result.Add(truncated);
                infos.Add(new TruncationInfo(msg.ToolName ?? "unknown", msg.Text!.Length, path));
            }
            else
            {
                result.Add(msg);
            }
        }
        return result;
    }

    private (ChatMessage Message, string FilePath) TruncateToolMessage(ChatMessage msg)
    {
        var content = msg.Text ?? "";
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var safeName = Regex.Replace(msg.ToolName ?? "unknown", "[^a-zA-Z0-9_-]", "_");
        var filePath = Path.Combine(_cfg.StorageDir, $"{timestamp}_{safeName}_{Guid.NewGuid():N}.txt");
        File.WriteAllText(filePath, content);

        var preview = content.Length > _cfg.PreviewLength ? content[.._cfg.PreviewLength] : content;
        var newText =
            "[工具结果过大，完整内容已保存到磁盘]\n" +
            $"文件: {filePath}\n" +
            $"预览（前 {_cfg.PreviewLength} 字符）:\n{preview}\n" +
            $"...（省略 {Math.Max(0, content.Length - _cfg.PreviewLength)} 字符）";

        return (msg with { Text = newText }, filePath);
    }
}
