using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VestiCode.Core.Tools.Builtin;

/// <summary>在文件内容中搜索正则表达式（只读工具）。</summary>
public sealed class GrepTool : ITool
{
    private const int MaxResults = 200;
    private const int SnippetLength = 300;

    private static readonly HashSet<string> SkipDirs = new(StringComparer.Ordinal)
    {
        ".git", ".hg", ".svn", "__pycache__", "node_modules", ".venv", "venv", "bin", "obj",
    };

    private static readonly HashSet<string> SkipExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pyc", ".pyo", ".exe", ".dll", ".so", ".o", ".bin", ".jpg", ".png", ".pdf",
    };

    private readonly string? _root;
    public GrepTool() { }
    public GrepTool(string root) => _root = root; // 团队成员：根植于其 worktree
    private string Root => _root ?? Directory.GetCurrentDirectory();

    public string Name => "grep";

    public string Description => "在文件内容中搜索正则表达式。返回文件名和匹配行。最多 200 条。";

    public ToolCategory Category => ToolCategory.Read;

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new ToolParameter("pattern", "string", "正则表达式，如 'class Main' 或 'using.*Linq'。"),
    ];

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, CancellationToken cancellationToken = default)
    {
        var pattern = arguments.GetString("pattern");
        var cwd = Root;

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.Compiled);
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Fail($"正则表达式无效: {ex.Message}");
        }

        var results = new List<string>();
        var truncated = false;

        foreach (var file in EnumerateFiles(cwd))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (SkipExtensions.Contains(Path.GetExtension(file)))
            {
                continue;
            }

            string text;
            try
            {
                text = await File.ReadAllTextAsync(file, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                continue; // 二进制 / 无权限，跳过
            }

            var rel = Path.GetRelativePath(cwd, file);
            var lineNo = 0;
            foreach (var line in text.Split('\n'))
            {
                lineNo++;
                if (regex.IsMatch(line))
                {
                    var trimmed = line.TrimEnd('\r');
                    var snippet = trimmed.Length > SnippetLength ? trimmed[..SnippetLength] : trimmed;
                    results.Add($"{rel}:{lineNo}: {snippet}");
                    if (results.Count >= MaxResults)
                    {
                        truncated = true;
                        break;
                    }
                }
            }
            if (truncated)
            {
                break;
            }
        }

        if (results.Count == 0)
        {
            return ToolResult.Ok("(无匹配)");
        }

        var header = $"找到 {results.Count} 条匹配";
        if (truncated)
        {
            header += $"（已截断到前 {MaxResults} 条）";
        }
        return ToolResult.Ok(header + "\n" + string.Join("\n", results));
    }

    /// <summary>递归枚举文件，跳过隐藏目录与常见无关目录。</summary>
    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] subDirs;
            string[] files;
            try
            {
                subDirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch (Exception)
            {
                continue; // 无权限目录，跳过
            }

            foreach (var sub in subDirs)
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') || SkipDirs.Contains(name))
                {
                    continue;
                }
                stack.Push(sub);
            }

            foreach (var file in files)
            {
                if (!Path.GetFileName(file).StartsWith('.'))
                {
                    yield return file;
                }
            }
        }
    }
}
