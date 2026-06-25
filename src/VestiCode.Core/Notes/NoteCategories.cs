namespace VestiCode.Core.Notes;

/// <summary>笔记分类、存储路径与更新 Prompt。</summary>
public static class NoteCategories
{
    /// <summary>用户级分类（存 ~/.vesticode/notes/）。</summary>
    public static readonly IReadOnlyDictionary<string, string> User = new Dictionary<string, string>
    {
        ["用户偏好"] = "user_preferences.md",
        ["纠正反馈"] = "corrections.md",
    };

    /// <summary>项目级分类（存 .vesticode/notes/）。</summary>
    public static readonly IReadOnlyDictionary<string, string> Project = new Dictionary<string, string>
    {
        ["项目知识"] = "project_knowledge.md",
        ["参考资料"] = "references.md",
    };

    /// <summary>各分类的职责说明（用于约束 LLM 只提炼本类信息）。</summary>
    public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>
    {
        ["用户偏好"] = "用户的工作习惯、编码偏好、工具选择等",
        ["纠正反馈"] = "用户纠正过的错误、用户明确不喜欢的做法",
        ["项目知识"] = "项目技术栈、架构决策、关键文件位置等",
        ["参考资料"] = "用户提到的文档链接、重要文章、参考资源",
    };

    public static string UserDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vesticode", "notes");

    public static string ProjectDir => Path.Combine(Directory.GetCurrentDirectory(), ".vesticode", "notes");

    public static string? FileFor(string category)
    {
        if (User.TryGetValue(category, out var u))
        {
            return u;
        }
        return Project.TryGetValue(category, out var p) ? p : null;
    }

    /// <summary>为<b>单个分类</b>构造增量更新 Prompt（只提炼并输出该分类的内容）。</summary>
    public static string BuildUpdatePrompt(string category, string currentNotes, string recentText)
    {
        var recent = recentText.Length > 8000 ? recentText[..8000] : recentText;
        var desc = Descriptions.TryGetValue(category, out var d) ? d : "";
        return $$"""
        你是一个笔记管理助手。你正在维护「{{category}}」这<b>一个</b>分类的笔记。
        该分类只记录：{{desc}}

        **规则**：
        - 只提炼属于「{{category}}」的信息，与本分类无关的内容一律忽略
        - 只记录新信息，已有信息不要重复
        - 新信息与已有内容冲突时，更新旧内容并标注日期（今天是 {{DateTime.Now:yyyy-MM-dd}}）
        - 保持简洁：每条一行，以 "- " 开头
        - 不要删除已有信息，除非与新信息明确冲突
        - 若最近对话没有与本分类相关的新信息，原样返回当前笔记

        ---

        当前「{{category}}」笔记：
        {{(string.IsNullOrEmpty(currentNotes) ? "(暂无)" : currentNotes)}}

        ---

        最近对话：
        {{recent}}

        ---

        只输出更新后的本分类笔记内容，以「## {{category}}」开头；不要包含其他分类，不要加额外说明。
        """;
    }
}
