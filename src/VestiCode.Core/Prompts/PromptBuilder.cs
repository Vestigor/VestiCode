namespace VestiCode.Core.Prompts;

/// <summary>
/// 从优先级模块拼装 System Prompt。额外 section 追加在模块之后（动态、不缓存）。
/// </summary>
public sealed class PromptBuilder
{
    private readonly IReadOnlyList<PromptModule> _modules = PromptModuleLoader.LoadModules();
    private readonly List<string> _extraSections = [];

    /// <summary>追加一个额外 section（位于所有模块之后）。</summary>
    public void AddSection(string text) => _extraSections.Add(text);

    public void ClearExtra() => _extraSections.Clear();

    /// <summary>拼装为纯文本 System Prompt。</summary>
    public string Build()
    {
        var parts = _modules.Select(m => m.Content).Concat(_extraSections);
        return string.Join("\n\n", parts);
    }

    /// <summary>拼装 System Prompt 并附加实时环境信息。</summary>
    public string BuildWithEnvironment()
        => Build() + "\n\n[Environment]\n" + EnvironmentProbe.Collect();
}
