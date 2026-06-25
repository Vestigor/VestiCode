using VestiCode.Core.Skills;

namespace VestiCode.UnitTests;

public sealed class SkillTests
{
    [Fact]
    public void LoadAll_LoadsBuiltinSkills()
    {
        var registry = new SkillRegistry(new SkillLoader());
        var metas = registry.LoadAll();
        var names = metas.Select(m => m.Name).ToHashSet();
        Assert.Contains("commit", names);
        Assert.Contains("review", names);
        Assert.Contains("test", names);
    }

    [Fact]
    public void Activate_PinsInstructions_AndParsesWhitelist()
    {
        var registry = new SkillRegistry(new SkillLoader());
        registry.LoadAll();

        var skill = registry.Activate("commit");
        Assert.NotNull(skill);
        Assert.Contains("Conventional Commits", skill!.Body);
        Assert.Contains("Skill: commit", registry.GetActiveInstructions());

        // commit 声明了工具白名单 → 交集非空且含 run_command。
        var whitelist = registry.GetActiveToolWhitelist();
        Assert.NotNull(whitelist);
        Assert.Contains("run_command", whitelist!);
        Assert.DoesNotContain("write_file", whitelist!);
    }

    [Fact]
    public void ClearActivated_RemovesInstructions()
    {
        var registry = new SkillRegistry(new SkillLoader());
        registry.LoadAll();
        registry.Activate("test");
        Assert.NotEqual("", registry.GetActiveInstructions());

        registry.ClearActivated();
        Assert.Equal("", registry.GetActiveInstructions());
        Assert.Null(registry.GetActiveToolWhitelist());
    }
}
