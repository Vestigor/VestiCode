using System.Text;
using Microsoft.Extensions.Logging;
using VestiCode.Core.Llm;

namespace VestiCode.Core.Notes;

/// <summary>
/// 自动笔记：每 N 轮调用 LLM，按四分类增量更新笔记（用户偏好/纠正反馈/项目知识/参考资料）。
/// </summary>
public sealed class AutoNoteManager
{
    private readonly ILlmProvider _provider;
    private readonly ILogger<AutoNoteManager> _logger;
    private readonly int _interval;
    private readonly List<string> _recent = [];
    private int _roundCounter;

    public AutoNoteManager(ILlmProvider provider, ILogger<AutoNoteManager> logger, int interval = 5)
    {
        _provider = provider;
        _logger = logger;
        _interval = interval;
        Directory.CreateDirectory(NoteCategories.UserDir);
        Directory.CreateDirectory(NoteCategories.ProjectDir);
    }

    /// <summary>记录一轮对话，供后续更新。</summary>
    public void RecordRound(string userMessage, string assistantMessage)
    {
        _roundCounter++;
        _recent.Add($"[user]: {userMessage}");
        _recent.Add($"[assistant]: {assistantMessage}");
        if (_recent.Count > 30)
        {
            _recent.RemoveRange(0, _recent.Count - 30);
        }
    }

    public bool ShouldUpdate() => _roundCounter > 0 && _roundCounter % _interval == 0;

    /// <summary>是否有尚未写入笔记的轮次（用于退出前的最终更新）。</summary>
    public bool HasPending => _recent.Count > 0;

    /// <summary>更新用户级与项目级全部分类，返回更新的文件数。</summary>
    public async Task<int> UpdateAllAsync(CancellationToken cancellationToken = default)
    {
        var recent = string.Join("\n", _recent);
        var updated = 0;

        foreach (var (category, file) in NoteCategories.User)
        {
            if (await UpdateOneAsync(category, Path.Combine(NoteCategories.UserDir, file), recent, cancellationToken).ConfigureAwait(false))
            {
                updated++;
            }
        }
        foreach (var (category, file) in NoteCategories.Project)
        {
            if (await UpdateOneAsync(category, Path.Combine(NoteCategories.ProjectDir, file), recent, cancellationToken).ConfigureAwait(false))
            {
                updated++;
            }
        }

        _recent.Clear();
        return updated;
    }

    /// <summary>读取某分类笔记内容。</summary>
    public string ReadNote(string category)
    {
        var file = NoteCategories.FileFor(category);
        if (file is null)
        {
            return $"未知分类: {category}";
        }
        foreach (var dir in new[] { NoteCategories.UserDir, NoteCategories.ProjectDir })
        {
            var path = Path.Combine(dir, file);
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }
        }
        return "(空)";
    }

    private async Task<bool> UpdateOneAsync(string category, string filePath, string recent, CancellationToken cancellationToken)
    {
        var current = File.Exists(filePath) ? await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false) : "";
        var prompt = NoteCategories.BuildUpdatePrompt(category, current, recent);

        try
        {
            var sb = new StringBuilder();
            await foreach (var item in _provider
                .ChatStreamAsync([ChatMessage.FromUser(prompt)], tools: null, cancellationToken)
                .ConfigureAwait(false))
            {
                if (item is TextDelta td)
                {
                    sb.Append(td.Text);
                }
            }

            var content = sb.ToString().Trim();
            if (content.Length > 0)
            {
                await File.WriteAllTextAsync(filePath, content + "\n", cancellationToken).ConfigureAwait(false);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "笔记更新失败: {File}", filePath);
        }
        return false;
    }
}
