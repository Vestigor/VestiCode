using VestiCode.Core.Commands;
using VestiCode.Core.Commands.Builtin;

namespace VestiCode.UnitTests;

public sealed class CommandTests
{
    [Fact]
    public void Parse_SplitsNameAndQuotedArgs()
    {
        var (name, args) = CommandDispatcher.Parse("/session load \"my session id\"");
        Assert.Equal("session", name);
        Assert.Equal(["load", "my session id"], args);
    }

    [Fact]
    public void Registry_DetectsNameConflict()
    {
        var registry = new CommandRegistry();
        registry.Register(new HelpCommand());
        Assert.Throws<InvalidOperationException>(() => registry.Register(new HelpCommand()));
    }

    [Fact]
    public void Registry_CompletesByPrefix()
    {
        var registry = new CommandRegistry();
        registry.RegisterRange([new HelpCommand(), new ClearCommand(), new CompressCommand()]);
        var completions = registry.GetCompletions("/c");
        Assert.Contains("/clear", completions);
        Assert.Contains("/compress", completions);
        Assert.DoesNotContain("/help", completions);
    }

    [Fact]
    public void Registry_LookupByAlias()
    {
        var registry = new CommandRegistry();
        registry.Register(new HelpCommand()); // alias "?"
        Assert.NotNull(registry.Lookup("?"));
        Assert.Equal("help", registry.Lookup("?")!.Name);
    }

    [Fact]
    public async Task ReviewCommand_InjectsPrompt()
    {
        var result = await new ReviewCommand().ExecuteAsync(["src/app.cs"], new StubUi());
        Assert.NotNull(result.InjectPrompt);
        Assert.Contains("src/app.cs", result.InjectPrompt);
    }

    /// <summary>仅满足接口的占位 UI（命令单测用）。</summary>
    private sealed class StubUi : IUiControl
    {
        public IReadOnlyList<CommandInfo> ListCommands() => [];
        public void WriteNotice(string message) { }
        public bool TogglePlanMode() => false;
        public bool GetPlanOnly() => false;
        public bool ToggleDispatchMode() => false;
        public string SetSecurityLevel(string levelName) => "";
        public string GetSecurityLevel() => "Normal";
        public int GetTokenCount() => 0;
        public string GetProviderLabel() => "stub";
        public string GetApiKeyMasked() => "sk-stub****stub";
        public void ClearConversation() { }
        public Task<string> TriggerCompressAsync() => Task.FromResult("");
        public IReadOnlyList<VestiCode.Core.Memory.SessionInfo> GetSessionList() => [];
        public string LoadSession(string sessionId) => "";
        public string NewSession() => "";
        public string DeleteSession(string sessionId) => "";
    }
}
