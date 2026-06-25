using System.Collections;
using VestiCode.Core.Llm;

namespace VestiCode.Core.Conversation;

/// <summary>
/// 对话历史（短期/工作记忆）：有序的 user/assistant/tool/system 消息列表。
/// 不含 System Prompt——那由 PromptBuilder 与 AgentLoop 管理。
/// </summary>
public sealed class ConversationHistory : IEnumerable<ChatMessage>
{
    private const double CharsPerToken = 3.5;

    private readonly List<ChatMessage> _messages = [];

    public int Count => _messages.Count;

    // -- 写入 ------------------------------------------------------------------

    public void AddUserMessage(string content)
    {
        if (!string.IsNullOrEmpty(content))
        {
            _messages.Add(ChatMessage.FromUser(content));
        }
    }

    public void AddAssistantMessage(string content)
    {
        if (!string.IsNullOrEmpty(content))
        {
            _messages.Add(ChatMessage.FromAssistant(content));
        }
    }

    /// <summary>追加原始消息（如带工具调用的 assistant、tool 结果）。空 assistant 消息被忽略。</summary>
    public void AddRawMessage(ChatMessage message)
    {
        if (message.Role == ChatRole.Assistant && IsEmptyAssistant(message))
        {
            return;
        }
        _messages.Add(message);
    }

    /// <summary>追加系统级上下文消息（如压缩摘要）。</summary>
    public void AddContextMessage(string content) =>
        _messages.Add(ChatMessage.FromSystem(content));

    public void ReplaceMessages(IEnumerable<ChatMessage> messages)
    {
        _messages.Clear();
        _messages.AddRange(messages);
    }

    public void Clear() => _messages.Clear();

    // -- 读取 ------------------------------------------------------------------

    /// <summary>返回消息列表（过滤掉无内容也无工具调用的 assistant 消息）。</summary>
    public IReadOnlyList<ChatMessage> GetMessages() =>
        _messages.Where(m => !(m.Role == ChatRole.Assistant && IsEmptyAssistant(m))).ToList();

    /// <summary>粗略估算 token 数（按字符数 / 3.5）。</summary>
    public int EstimatedTokenCount()
    {
        var chars = 0;
        foreach (var m in _messages)
        {
            chars += m.Text?.Length ?? 0;
            foreach (var call in m.ToolCalls)
            {
                chars += call.Arguments.ToJsonString().Length + call.Name.Length;
            }
        }
        return Math.Max(0, (int)(chars / CharsPerToken));
    }

    private static bool IsEmptyAssistant(ChatMessage m) =>
        string.IsNullOrEmpty(m.Text) && m.ToolCalls.Count == 0;

    public IEnumerator<ChatMessage> GetEnumerator() => _messages.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
