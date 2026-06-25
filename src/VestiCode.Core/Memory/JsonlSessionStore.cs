using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using VestiCode.Core.Conversation;
using VestiCode.Core.Llm;

namespace VestiCode.Core.Memory;

/// <summary>会话概要（用于列表展示）。</summary>
/// <param name="Id">会话 ID。</param>
/// <param name="Title">标题（首条用户消息截断）。</param>
/// <param name="Provider">Provider 名。</param>
/// <param name="Model">模型。</param>
/// <param name="MessageCount">消息数。</param>
/// <param name="LastActiveAt">最后活跃时间。</param>
public sealed record SessionInfo(
    string Id, string Title, string Provider, string Model, int MessageCount, DateTimeOffset LastActiveAt);

/// <summary>
/// JSONL 会话持久化（长期记忆）。每个会话两个文件：
/// <c>{id}.jsonl</c>（追加式消息日志，O(1) 写、崩溃只丢最后一行）与
/// <c>{id}.meta.json</c>（概要，供列表）。加载时跳过损坏行、在未配对的 tool_use 处截断、
/// 跨度 &gt; 30 分钟插入时间提醒。
/// </summary>
public sealed class JsonlSessionStore
{
    private const int TimeGapMinutes = 30;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _sessionsDir;
    private string? _currentId;

    public JsonlSessionStore(string? sessionsDir = null)
    {
        _sessionsDir = sessionsDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vesticode", "sessions");
        Directory.CreateDirectory(_sessionsDir);
    }

    public string? CurrentId => _currentId;

    /// <summary>开启一个新会话并返回其 ID。</summary>
    public string NewSession()
    {
        _currentId = Guid.NewGuid().ToString("N")[..12];
        return _currentId;
    }

    /// <summary>全量保存当前历史（每轮结束的安全网，覆盖写）。</summary>
    public void Save(ConversationHistory history, string providerName, string model)
    {
        _currentId ??= NewSession();
        var now = DateTimeOffset.UtcNow;
        var messages = history.GetMessages();

        using (var writer = new StreamWriter(PathFor(_currentId), append: false))
        {
            foreach (var msg in messages)
            {
                writer.WriteLine(JsonSerializer.Serialize(StoredMessage.From(msg, now), JsonOptions));
            }
        }

        // 保留首次创建时间。
        var existing = ReadMeta(_currentId);
        var createdAt = existing is not null && existing.CreatedAt != default ? existing.CreatedAt : now;

        WriteMeta(new SessionMeta
        {
            Id = _currentId,
            Title = GuessTitle(messages),
            Provider = providerName,
            Model = model,
            MessageCount = messages.Count,
            CreatedAt = createdAt,
            LastActiveAt = now,
        });
    }

    /// <summary>加载会话（带恢复）。<paramref name="sessionId"/> 为空时取最近一个。</summary>
    public (ConversationHistory History, string Provider, string Model)? Load(string? sessionId = null)
    {
        var sid = ResolveId(sessionId);
        if (sid is null || !File.Exists(PathFor(sid)))
        {
            return null;
        }

        // 逐行读取，跳过损坏行。
        var stored = new List<StoredMessage>();
        foreach (var line in File.ReadLines(PathFor(sid)))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            try
            {
                var msg = JsonSerializer.Deserialize<StoredMessage>(line, JsonOptions);
                if (msg is not null)
                {
                    stored.Add(msg);
                }
            }
            catch (JsonException)
            {
                // 跳过损坏行（崩溃时可能只写了一半）。
            }
        }

        stored = TruncateUnpaired(stored);

        var history = new ConversationHistory();
        var rebuilt = new List<ChatMessage>();
        DateTimeOffset? prev = null;
        foreach (var s in stored)
        {
            if (prev is { } p && s.Timestamp - p > TimeSpan.FromMinutes(TimeGapMinutes))
            {
                var hours = (s.Timestamp - p).TotalHours;
                rebuilt.Add(ChatMessage.FromSystem($"[时间跨度提醒] 距上次活跃约 {hours:0.0} 小时，以下是新消息。"));
            }
            prev = s.Timestamp;
            rebuilt.Add(s.ToChatMessage());
        }
        history.ReplaceMessages(rebuilt);

        _currentId = sid;
        var meta = ReadMeta(sid);
        return (history, meta?.Provider ?? "", meta?.Model ?? "");
    }

    /// <summary>列出所有会话概要（按最后活跃时间倒序）。</summary>
    public IReadOnlyList<SessionInfo> ListSessions()
    {
        if (!Directory.Exists(_sessionsDir))
        {
            return [];
        }
        return Directory.EnumerateFiles(_sessionsDir, "*.meta.json")
            .Select(f => ReadMeta(Path.GetFileNameWithoutExtension(f).Replace(".meta", "")))
            .Where(m => m is not null)
            .Select(m => new SessionInfo(m!.Id, m.Title, m.Provider, m.Model, m.MessageCount, m.LastActiveAt))
            .OrderByDescending(s => s.LastActiveAt)
            .ToList();
    }

    /// <summary>删除会话及其 meta 文件。</summary>
    public bool Delete(string sessionId)
    {
        var sid = ResolveId(sessionId);
        if (sid is null)
        {
            return false;
        }
        var deleted = false;
        foreach (var path in new[] { PathFor(sid), MetaPathFor(sid) })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                deleted = true;
            }
        }
        return deleted;
    }

    // -- 内部 ------------------------------------------------------------------

    private string PathFor(string sid) => Path.Combine(_sessionsDir, $"{Sanitize(sid)}.jsonl");

    private string MetaPathFor(string sid) => Path.Combine(_sessionsDir, $"{Sanitize(sid)}.meta.json");

    private static string Sanitize(string sid) => sid.Replace('\\', '_').Replace('/', '_');

    private string? ResolveId(string? sessionId)
    {
        if (!string.IsNullOrEmpty(sessionId))
        {
            if (File.Exists(PathFor(sessionId)))
            {
                return sessionId;
            }
            // 前缀匹配，唯一才接受。
            var matches = Directory.EnumerateFiles(_sessionsDir, $"{Sanitize(sessionId)}*.jsonl").ToList();
            return matches.Count == 1 ? Path.GetFileNameWithoutExtension(matches[0]) : null;
        }

        // 最近活跃的会话。
        return ListSessions().FirstOrDefault()?.Id;
    }

    private void WriteMeta(SessionMeta meta) =>
        File.WriteAllText(MetaPathFor(meta.Id),
            JsonSerializer.Serialize(meta, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }));

    private SessionMeta? ReadMeta(string sid)
    {
        var path = MetaPathFor(sid);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<SessionMeta>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 若结尾存在未配对的 tool_use（assistant 请求了工具但没有对应的 tool 结果，
    /// 常见于中途崩溃），则从最早那条未配对的 assistant 消息处截断，保证配对完整。
    /// </summary>
    private static List<StoredMessage> TruncateUnpaired(List<StoredMessage> messages)
    {
        // 记录每个仍未配对的工具调用 ID 对应的 assistant 消息下标。
        var openCallIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            if (msg.Role == nameof(ChatRole.Assistant) && msg.ToolCalls is { Count: > 0 })
            {
                foreach (var tc in msg.ToolCalls)
                {
                    openCallIndex[tc.Id] = i;
                }
            }
            else if (msg.Role == nameof(ChatRole.Tool) && msg.ToolCallId is not null)
            {
                openCallIndex.Remove(msg.ToolCallId);
            }
        }

        // 全部配对 → 原样返回；否则从最早未配对处截断。
        return openCallIndex.Count == 0 ? messages : messages[..openCallIndex.Values.Min()];
    }

    private static string GuessTitle(IReadOnlyList<ChatMessage> messages)
    {
        var first = messages.FirstOrDefault(m => m.Role == ChatRole.User)?.Text;
        if (string.IsNullOrEmpty(first))
        {
            return "未命名会话";
        }
        return first.Length > 60 ? first[..60] : first;
    }

    // -- 持久化 DTO ------------------------------------------------------------

    private sealed record StoredToolCall(string Id, string Name, JsonObject Arguments);

    private sealed record StoredMessage(
        string Role,
        string? Text,
        List<StoredToolCall>? ToolCalls,
        string? ToolCallId,
        string? ToolName,
        DateTimeOffset Timestamp)
    {
        public static StoredMessage From(ChatMessage m, DateTimeOffset ts) => new(
            m.Role.ToString(),
            m.Text,
            m.ToolCalls.Count > 0
                ? m.ToolCalls.Select(c => new StoredToolCall(c.Id, c.Name, (JsonObject)c.Arguments.DeepClone())).ToList()
                : null,
            m.ToolCallId,
            m.ToolName,
            ts);

        public ChatMessage ToChatMessage()
        {
            var role = Enum.TryParse<ChatRole>(Role, out var r) ? r : ChatRole.User;
            return new ChatMessage
            {
                Role = role,
                Text = Text,
                ToolCalls = ToolCalls?.Select(c => new ToolCall(c.Id, c.Name, (JsonObject)c.Arguments.DeepClone())).ToList()
                            ?? (IReadOnlyList<ToolCall>)[],
                ToolCallId = ToolCallId,
                ToolName = ToolName,
            };
        }
    }

    private sealed class SessionMeta
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Provider { get; set; } = "";
        public string Model { get; set; } = "";
        public int MessageCount { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset LastActiveAt { get; set; }
    }
}
