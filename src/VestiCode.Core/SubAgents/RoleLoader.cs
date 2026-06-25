using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace VestiCode.Core.SubAgents;

/// <summary>
/// 加载子 Agent 角色（三级：内置嵌入 → 全局 <c>~/.vesticode/roles/</c> → 项目 <c>./.vesticode/roles/</c>，
/// 同名后者覆盖前者），解析 frontmatter + 正文。
/// </summary>
public sealed partial class RoleLoader
{
    private const string BuiltinPrefix = "VestiCode.Core.SubAgents.Roles.";

    private static string UserDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vesticode", "roles");

    private static string ProjectDir => Path.Combine(Directory.GetCurrentDirectory(), ".vesticode", "roles");

    [GeneratedRegex(@"^---\s*\n(.*?)\n---\s*\n", RegexOptions.Singleline)]
    private static partial Regex FrontmatterRegex();

    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    /// <summary>加载全部角色（内置 → 全局 → 项目，同名后者覆盖前者）。</summary>
    public IReadOnlyDictionary<string, SubAgentRole> LoadAll()
    {
        var index = new Dictionary<string, SubAgentRole>(StringComparer.Ordinal);

        // 1) 内置（嵌入资源）
        var assembly = typeof(RoleLoader).Assembly;
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (name.StartsWith(BuiltinPrefix, StringComparison.Ordinal) && name.EndsWith(".md", StringComparison.Ordinal))
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                if (Parse(reader.ReadToEnd()) is { } role)
                {
                    index[role.Name] = role;
                }
            }
        }

        // 2) 全局，3) 项目（同名覆盖）
        ScanDir(UserDir, index);
        ScanDir(ProjectDir, index);
        return index;
    }

    private static void ScanDir(string directory, Dictionary<string, SubAgentRole> index)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }
        foreach (var path in Directory.EnumerateFiles(directory, "*.md").OrderBy(p => p, StringComparer.Ordinal))
        {
            try
            {
                if (Parse(File.ReadAllText(path)) is { } role)
                {
                    index[role.Name] = role;
                }
            }
            catch (Exception)
            {
                // 单个角色文件损坏不影响其余加载
            }
        }
    }

    private static SubAgentRole? Parse(string text)
    {
        var match = FrontmatterRegex().Match(text);
        if (!match.Success)
        {
            return null;
        }

        Dictionary<string, object> fm;
        try
        {
            fm = Yaml.Deserialize<Dictionary<string, object>>(match.Groups[1].Value) ?? [];
        }
        catch (Exception)
        {
            return null;
        }

        var name = GetString(fm, "name");
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        return new SubAgentRole
        {
            Name = name,
            Description = GetString(fm, "description") ?? "",
            ToolsAllow = GetList(fm, "tools_allow"),
            ToolsDeny = GetList(fm, "tools_deny") ?? [],
            MaxRounds = GetInt(fm, "max_rounds", 5),
            Permission = GetString(fm, "permission") ?? "normal",
            SystemPrompt = text[match.Length..].Trim(),
        };
    }

    private static string? GetString(Dictionary<string, object> fm, string key)
        => fm.TryGetValue(key, out var v) && v is not null ? v.ToString() : null;

    private static int GetInt(Dictionary<string, object> fm, string key, int fallback)
        => fm.TryGetValue(key, out var v) && int.TryParse(v?.ToString(), out var n) ? n : fallback;

    private static IReadOnlyList<string>? GetList(Dictionary<string, object> fm, string key)
        => fm.TryGetValue(key, out var v) && v is IEnumerable<object> items
            ? items.Select(i => i.ToString() ?? "").Where(s => s.Length > 0).ToList()
            : null;
}
