using System.Text.Json.Nodes;
using Microsoft.Extensions.FileSystemGlobbing;

namespace VestiCode.Core.Tools.Builtin;

/// <summary>按 glob 模式查找文件（只读工具）。</summary>
public sealed class GlobTool : ITool
{
    private const int MaxResults = 200;

    private readonly string? _root;
    public GlobTool() { }
    public GlobTool(string root) => _root = root; // 团队成员：根植于其 worktree
    private string Root => _root ?? Directory.GetCurrentDirectory();

    public string Name => "glob";

    public string Description => "按 glob 模式查找文件。返回匹配的文件路径列表。最多返回 200 条。";

    public ToolCategory Category => ToolCategory.Read;

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new ToolParameter("pattern", "string", "Glob 模式，如 '**/*.cs' 或 'src/**/*.ts'。"),
    ];

    public Task<ToolResult> ExecuteAsync(JsonObject arguments, CancellationToken cancellationToken = default)
    {
        var pattern = arguments.GetString("pattern");
        var cwd = Root;

        List<string> matches;
        try
        {
            var matcher = new Matcher(StringComparison.Ordinal);
            matcher.AddInclude(pattern);
            matches = matcher.GetResultsInFullPath(cwd)
                .Select(full => Path.GetRelativePath(cwd, full))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Glob 模式无效: {ex.Message}"));
        }

        if (matches.Count == 0)
        {
            return Task.FromResult(ToolResult.Ok("(无匹配文件)"));
        }

        var truncated = matches.Count > MaxResults;
        if (truncated)
        {
            matches = matches.Take(MaxResults).ToList();
        }

        var summary = truncated
            ? $"找到 {matches.Count} 个匹配（已截断到前 {MaxResults} 条）\n"
            : $"找到 {matches.Count} 个匹配\n";
        return Task.FromResult(ToolResult.Ok(summary + string.Join("\n", matches)));
    }
}
