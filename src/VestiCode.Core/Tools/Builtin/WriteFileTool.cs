using System.Text;
using System.Text.Json.Nodes;

namespace VestiCode.Core.Tools.Builtin;

/// <summary>写入文件（文件已存在则失败，提示改用 edit_file）。</summary>
public sealed class WriteFileTool : ITool
{
    private readonly string? _root;
    public WriteFileTool() { }
    public WriteFileTool(string root) => _root = root; // 团队成员：根植于其 worktree
    private string Root => _root ?? Directory.GetCurrentDirectory();

    public string Name => "write_file";

    public string Description => "将内容写入文件。如果文件已存在，操作失败并提示。目录不存在时自动创建。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new ToolParameter("path", "string", "文件路径，相对于工作目录。"),
        new ToolParameter("content", "string", "要写入的完整文本内容。"),
    ];

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, CancellationToken cancellationToken = default)
    {
        var path = arguments.GetString("path");
        var content = arguments.GetString("content");

        string resolved;
        try
        {
            resolved = WorkspacePath.Resolve(path, Root);
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Fail(ex.Message);
        }

        if (File.Exists(resolved))
        {
            return ToolResult.Fail($"文件已存在: {path}。请使用 edit_file 修改内容，或先删除再写入。");
        }

        var dir = Path.GetDirectoryName(resolved);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(resolved, content, new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);
        return ToolResult.Ok($"已写入文件: {path} ({content.Length} 字符)");
    }
}
