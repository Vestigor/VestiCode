using System.Text;
using System.Text.Json.Nodes;

namespace VestiCode.Core.Tools.Builtin;

/// <summary>
/// 精确匹配替换：old_string 必须在文件中<b>恰好出现一次</b>，
/// 0 次或多次都失败并给出诊断，确保编辑无歧义。
/// </summary>
public sealed class EditFileTool : ITool
{
    private readonly string? _root;
    public EditFileTool() { }
    public EditFileTool(string root) => _root = root; // 团队成员：根植于其 worktree
    private string Root => _root ?? Directory.GetCurrentDirectory();

    public string Name => "edit_file";

    public string Description =>
        "精确替换文件中的某段文本。old_string 必须在文件中恰好出现一次，否则操作失败。匹配区分大小写、不忽略空白。" +
        "注意：old_string 应是文件的<b>原始文本</b>，不要包含 read_file 输出的行号前缀。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new ToolParameter("path", "string", "文件路径，相对于工作目录。"),
        new ToolParameter("old_string", "string", "要替换的原文。必须与文件中内容逐字符完全匹配。"),
        new ToolParameter("new_string", "string", "替换后的新文本。"),
    ];

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, CancellationToken cancellationToken = default)
    {
        var path = arguments.GetString("path");
        var oldString = arguments.GetString("old_string");
        var newString = arguments.GetString("new_string");

        string resolved;
        try
        {
            resolved = WorkspacePath.Resolve(path, Root);
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Fail(ex.Message);
        }

        if (!File.Exists(resolved))
        {
            return ToolResult.Fail($"文件不存在: {path}");
        }

        var original = await File.ReadAllTextAsync(resolved, cancellationToken).ConfigureAwait(false);
        var count = CountOccurrences(original, oldString);

        if (count == 0)
        {
            var preview = original.Length > 500 ? original[..500] : original;
            return ToolResult.Fail(
                "未找到匹配的原文。请确认 old_string 与文件中的文本逐字符一致（包括空白和换行）。" +
                $"文件内容预览（前 500 字符）:\n{preview}");
        }

        if (count > 1)
        {
            return ToolResult.Fail(
                $"找到 {count} 处匹配，old_string 必须在文件中只出现一次。请提供足够长的上下文以确保唯一性。");
        }

        // 只替换第一处（此时已确定全文唯一）。
        var index = original.IndexOf(oldString, StringComparison.Ordinal);
        var updated = string.Concat(original.AsSpan(0, index), newString, original.AsSpan(index + oldString.Length));

        await File.WriteAllTextAsync(resolved, updated, new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);
        return ToolResult.Ok($"已编辑文件: {path}（替换了 1 处）");
    }

    /// <summary>统计 <paramref name="needle"/> 在 <paramref name="haystack"/> 中按序数比较的非重叠出现次数。</summary>
    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle))
        {
            return 0;
        }

        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
