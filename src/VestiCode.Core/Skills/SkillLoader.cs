using System.Reflection;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace VestiCode.Core.Skills;

/// <summary>
/// 三级 Skill 加载：内置（嵌入资源）&lt; 用户（~/.vesticode/skills）&lt; 项目（.vesticode/skills），
/// 同名后者覆盖前者。解析 YAML frontmatter + Markdown 正文。
/// </summary>
public sealed partial class SkillLoader
{
    private const string BuiltinPrefix = "VestiCode.Core.Skills.Builtin.";

    private static readonly string UserDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vesticode", "skills");

    private static string ProjectDir => Path.Combine(Directory.GetCurrentDirectory(), ".vesticode", "skills");

    [GeneratedRegex(@"^---\s*\n(.*?)\n---\s*\n", RegexOptions.Singleline)]
    private static partial Regex FrontmatterRegex();

    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    /// <summary>加载所有 Skill（同名按优先级覆盖）。</summary>
    public IReadOnlyList<SkillDefinition> LoadAll()
    {
        var index = new Dictionary<string, SkillDefinition>(StringComparer.Ordinal);

        foreach (var skill in LoadBuiltin())
        {
            index[skill.Meta.Name] = skill;
        }
        ScanDir(UserDir, index);
        ScanDir(ProjectDir, index);

        return index.Values.ToList();
    }

    /// <summary>从源（文件路径或内置资源名）热加载单个 Skill。</summary>
    public SkillDefinition? LoadOne(string source)
    {
        if (source.StartsWith(BuiltinPrefix, StringComparison.Ordinal))
        {
            var text = ReadEmbedded(source);
            return text is null ? null : Parse(text, source);
        }
        return File.Exists(source) ? Parse(File.ReadAllText(source), source) : null;
    }

    private static IEnumerable<SkillDefinition> LoadBuiltin()
    {
        var assembly = typeof(SkillLoader).Assembly;
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(BuiltinPrefix, StringComparison.Ordinal) || !name.EndsWith(".md", StringComparison.Ordinal))
            {
                continue;
            }
            var text = ReadEmbedded(name);
            if (text is not null && Parse(text, name) is { } skill)
            {
                yield return skill;
            }
        }
    }

    private static void ScanDir(string directory, Dictionary<string, SkillDefinition> index)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }
        foreach (var md in Directory.EnumerateFiles(directory, "*.md").OrderBy(p => p, StringComparer.Ordinal))
        {
            if (Parse(File.ReadAllText(md), md) is { } skill)
            {
                index[skill.Meta.Name] = skill;
            }
        }
    }

    private static SkillDefinition? Parse(string text, string source)
    {
        var match = FrontmatterRegex().Match(text);
        if (!match.Success)
        {
            return null; // 缺少 frontmatter
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

        var meta = new SkillMeta
        {
            Name = name,
            Description = GetString(fm, "description") ?? "",
            Mode = ParseEnum(GetString(fm, "mode"), SkillMode.Shared),
            Model = GetString(fm, "model"),
            Tools = GetList(fm, "tools"),
            HistoryCarry = ParseEnum(GetString(fm, "history_carry"), HistoryCarry.Full),
            RecentCount = 10,
            Source = source,
        };

        return new SkillDefinition { Meta = meta, Body = text[match.Length..].Trim() };
    }

    private static string? ReadEmbedded(string resource)
    {
        using var stream = typeof(SkillLoader).Assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            return null;
        }
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string? GetString(Dictionary<string, object> fm, string key)
        => fm.TryGetValue(key, out var v) && v is not null ? v.ToString() : null;

    private static IReadOnlyList<string>? GetList(Dictionary<string, object> fm, string key)
    {
        if (!fm.TryGetValue(key, out var v) || v is null)
        {
            return null; // 缺省 = 全部工具
        }
        return v switch
        {
            IEnumerable<object> items => items.Select(i => i.ToString() ?? "").Where(s => s.Length > 0).ToList(),
            _ => null,
        };
    }

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback) where TEnum : struct
        => Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) ? parsed : fallback;
}
