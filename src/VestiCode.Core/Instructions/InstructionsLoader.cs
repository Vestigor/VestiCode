using System.Text.RegularExpressions;

namespace VestiCode.Core.Instructions;

/// <summary>
/// 项目/用户指令加载：读取 <c>VESTICODE.md</c>（项目级，cwd）与
/// <c>~/.vesticode/instructions.md</c>（用户级），解析 <c>@include(path)</c> 指令（最多 3 层）。
/// 项目级优先（排在输出前面）。这是“项目记忆”的一部分。
/// </summary>
public sealed partial class InstructionsLoader
{
    private const int MaxIncludeDepth = 3;

    [GeneratedRegex(@"^@include\((.+)\)$", RegexOptions.Multiline)]
    private static partial Regex IncludeRegex();

    private static string ProjectFile => Path.Combine(Directory.GetCurrentDirectory(), "VESTICODE.md");

    private static string UserFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vesticode", "instructions.md");

    /// <summary>加载并合并项目级 + 用户级指令。</summary>
    public string Load()
    {
        var parts = new List<string>();
        if (File.Exists(ProjectFile))
        {
            parts.Add(LoadWithIncludes(ProjectFile, 0));
        }
        if (File.Exists(UserFile))
        {
            parts.Add(LoadWithIncludes(UserFile, 0));
        }
        return string.Join("\n\n", parts);
    }

    private string LoadWithIncludes(string filePath, int depth)
    {
        if (depth > MaxIncludeDepth)
        {
            throw new InvalidOperationException($"@include 嵌套深度超过 {MaxIncludeDepth} 层: {filePath}");
        }

        var content = File.ReadAllText(filePath);
        var baseDir = Path.GetFullPath(Path.GetDirectoryName(filePath)!);

        return IncludeRegex().Replace(content, match =>
        {
            var includePath = match.Groups[1].Value.Trim();
            var full = Path.GetFullPath(Path.Combine(baseDir, includePath));

            // 防越界：必须位于基目录或用户主目录内。
            var home = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            if (!full.StartsWith(baseDir, StringComparison.Ordinal) && !full.StartsWith(home, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"@include 路径越界: {includePath}");
            }
            if (!File.Exists(full))
            {
                throw new InvalidOperationException($"@include 文件不存在: {includePath}");
            }
            return LoadWithIncludes(full, depth + 1);
        });
    }
}
