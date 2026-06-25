using VestiCode.Core.Worktree;

namespace VestiCode.UnitTests;

public sealed class WorktreeTests
{
    [Theory]
    [InlineData("feature-x", true)]
    [InlineData("team/member1", true)]
    [InlineData("", false)]
    [InlineData("../escape", false)]
    [InlineData("bad name", false)]
    [InlineData("has.dot", false)]
    public void ValidateName(string name, bool expectedValid)
        => Assert.Equal(expectedValid, WorktreeValidator.ValidateName(name).Valid);

    [Fact]
    public void NameToBranch_AndDir()
    {
        Assert.Equal("vesticode/feature-x", WorktreeValidator.NameToBranch("feature-x"));
        Assert.Equal("team-m1", WorktreeValidator.NameToDirName("team/m1"));
    }
}
