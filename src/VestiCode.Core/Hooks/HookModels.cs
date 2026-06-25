namespace VestiCode.Core.Hooks;

/// <summary>12 种生命周期事件。</summary>
public enum HookEvent
{
    SessionStart,
    SessionEnd,
    RoundStart,
    RoundEnd,
    MessagePreSend,
    MessagePostReceive,
    ToolPreExec,
    ToolPostExec,
    SystemStartup,
    SystemShutdown,
    SystemError,
    SystemCompress,
}

/// <summary>条件操作符。</summary>
public enum HookOperator
{
    Exact,
    Not,
    Regex,
    Glob,
}

/// <summary>条件组合逻辑。</summary>
public enum MatchMode
{
    All,
    Any,
}

/// <summary>动作类型。</summary>
public enum ActionType
{
    Shell,
    PromptInject,
    Http,
    SubAgent,
}

/// <summary>单条条件规则（field 支持点路径，如 <c>params.command</c>）。</summary>
public sealed class ConditionRule
{
    public required string Field { get; init; }
    public required HookOperator Operator { get; init; }
    public required string Value { get; init; }
}

/// <summary>条件（多条规则 + ALL/ANY 组合）。</summary>
public sealed class HookCondition
{
    public MatchMode Match { get; init; } = MatchMode.All;
    public IReadOnlyList<ConditionRule> Rules { get; init; } = [];
}

/// <summary>一个动作。</summary>
public sealed class HookAction
{
    public required ActionType Type { get; init; }
    public string Command { get; init; } = "";   // shell
    public string Text { get; init; } = "";       // prompt_inject
    public string Url { get; init; } = "";         // http
    public string Method { get; init; } = "POST";  // http
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public string Body { get; init; } = "";        // http
    public string Task { get; init; } = "";        // sub_agent
}

/// <summary>执行控制。</summary>
public sealed class HookControl
{
    public bool Once { get; init; }
    public bool Async { get; init; }
    public double Timeout { get; init; } = 30.0;
}

/// <summary>一条 Hook 规则（事件 + 条件 + 动作 + 控制）。</summary>
public sealed class HookRule
{
    public required HookEvent Event { get; init; }
    public HookCondition? Condition { get; init; }
    public IReadOnlyList<HookAction> Actions { get; init; } = [];
    public HookControl Control { get; init; } = new();
    public string Name { get; init; } = "";

    /// <summary>是否为拦截类事件（其动作可返回拒绝原因）。</summary>
    public bool IsIntercept => Event == HookEvent.ToolPreExec;
}
