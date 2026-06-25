using System.Reflection;
using System.Text.RegularExpressions;

namespace VestiCode.Core.Prompts;

/// <summary>一个 Prompt 模块。</summary>
/// <param name="Priority">优先级（数字越小越靠前）。</param>
/// <param name="Name">模块名（由文件名 <c>NN-name.txt</c> 推导）。</param>
/// <param name="Content">正文。</param>
public sealed record PromptModule(int Priority, string Name, string Content);

/// <summary>从嵌入资源加载 Prompt 模块与注入模板。</summary>
public static partial class PromptModuleLoader
{
    private const string ModulesPrefix = "VestiCode.Core.Prompts.Modules.";
    private const string InjectionsPrefix = "VestiCode.Core.Prompts.Injections.";

    [GeneratedRegex(@"^(\d+)-(.+)\.txt$")]
    private static partial Regex ModuleNameRegex();

    /// <summary>加载所有模块（按优先级升序）。</summary>
    public static IReadOnlyList<PromptModule> LoadModules()
    {
        var assembly = typeof(PromptModuleLoader).Assembly;
        var modules = new List<PromptModule>();

        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ModulesPrefix, StringComparison.Ordinal))
            {
                continue;
            }
            var fileName = resource[ModulesPrefix.Length..];
            var match = ModuleNameRegex().Match(fileName);
            if (!match.Success)
            {
                continue;
            }
            var priority = int.Parse(match.Groups[1].Value);
            var name = match.Groups[2].Value;
            modules.Add(new PromptModule(priority, name, ReadResource(assembly, resource).Trim()));
        }

        modules.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        return modules;
    }

    /// <summary>按名加载注入模板（不带扩展名），不存在返回空串。</summary>
    public static string LoadInjection(string name)
    {
        var assembly = typeof(PromptModuleLoader).Assembly;
        var resource = InjectionsPrefix + name + ".txt";
        return assembly.GetManifestResourceInfo(resource) is null
            ? ""
            : ReadResource(assembly, resource).Trim();
    }

    private static string ReadResource(Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"嵌入资源缺失: {resource}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
