namespace VestiCode.Core.Skills;

/// <summary>
/// Skill 生命周期管理：阶段1 加载（仅名字+描述注入对话），阶段2 激活（载入完整 SOP 并固定）。
/// </summary>
public sealed class SkillRegistry(SkillLoader loader)
{
    private readonly Dictionary<string, SkillDefinition> _all = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SkillDefinition> _activated = new(StringComparer.Ordinal);

    /// <summary>阶段1：扫描三级目录，返回所有 Skill 的元数据。</summary>
    public IReadOnlyList<SkillMeta> LoadAll()
    {
        _all.Clear();
        foreach (var skill in loader.LoadAll())
        {
            _all[skill.Meta.Name] = skill;
        }
        return _all.Values.Select(s => s.Meta).ToList();
    }

    public IReadOnlyList<SkillMeta> ListAvailable() => _all.Values.Select(s => s.Meta).ToList();

    /// <summary>校验各 Skill 的工具白名单是否都引用了已知工具，返回告警信息。</summary>
    public IReadOnlyList<string> ValidateWhitelists(IReadOnlyCollection<string> knownTools)
    {
        var warnings = new List<string>();
        foreach (var skill in _all.Values)
        {
            if (skill.Meta.Tools is null)
            {
                continue;
            }
            var unknown = skill.Meta.Tools.Where(t => !knownTools.Contains(t)).ToList();
            if (unknown.Count > 0)
            {
                warnings.Add($"Skill '{skill.Meta.Name}' 声明了未知工具: {string.Join(", ", unknown)}");
            }
        }
        return warnings;
    }

    /// <summary>阶段2：激活（热重载源文件）并固定 Skill。</summary>
    public SkillDefinition? Activate(string name)
    {
        if (!_all.TryGetValue(name, out var skill))
        {
            return null;
        }
        if (!string.IsNullOrEmpty(skill.Meta.Source) && loader.LoadOne(skill.Meta.Source) is { } reloaded)
        {
            skill = reloaded;
        }
        _activated[name] = skill;
        return skill;
    }

    public bool Deactivate(string name) => _activated.Remove(name);

    public void ClearActivated() => _activated.Clear();

    public IReadOnlyList<SkillDefinition> Activated => _activated.Values.ToList();

    /// <summary>所有已激活 Skill 的合并指令（固定注入对话）。</summary>
    public string GetActiveInstructions()
    {
        if (_activated.Count == 0)
        {
            return "";
        }
        return string.Join("\n\n", _activated.Values.Select(s => $"## Skill: {s.Meta.Name}\n{s.Body}"));
    }

    /// <summary>
    /// 已激活 Skill 工具白名单的交集；任一 Skill 允许全部工具（Tools == null）则返回 null。
    /// </summary>
    public IReadOnlyCollection<string>? GetActiveToolWhitelist()
    {
        var sets = new List<HashSet<string>>();
        foreach (var skill in _activated.Values)
        {
            if (skill.Meta.Tools is null)
            {
                return null;
            }
            sets.Add([.. skill.Meta.Tools]);
        }
        if (sets.Count == 0)
        {
            return null;
        }
        var intersection = new HashSet<string>(sets[0], StringComparer.Ordinal);
        foreach (var set in sets.Skip(1))
        {
            intersection.IntersectWith(set);
        }
        return intersection;
    }
}
