namespace VestiCode.Core.Hooks;

/// <summary>
/// Hook 引擎：在 Agent 生命周期节点触发匹配的规则。
/// 对拦截类事件（tool_pre_exec），返回第一条 prompt_inject 拒绝原因，调用方据此阻止工具。
/// </summary>
public sealed class HookEngine(IReadOnlyList<HookRule> rules, ActionExecutor actions)
{
    private readonly HashSet<string> _firedOnce = new(StringComparer.Ordinal);

    /// <summary>当前是否注册了任何规则。</summary>
    public bool HasRules => rules.Count > 0;

    /// <summary>触发 <paramref name="evt"/> 的所有匹配规则。拦截事件返回拒绝原因（否则 null）。</summary>
    public async Task<string?> FireAsync(HookEvent evt, IReadOnlyDictionary<string, object?>? context = null)
    {
        var ctx = new Dictionary<string, object?>(context ?? new Dictionary<string, object?>())
        {
            ["_event"] = evt.ToString(),
        };

        string? rejection = null;

        foreach (var rule in rules)
        {
            if (rule.Event != evt)
            {
                continue;
            }
            if (rule.Control.Once && _firedOnce.Contains(rule.Name))
            {
                continue;
            }
            if (!ConditionEvaluator.Evaluate(rule.Condition, ctx))
            {
                continue;
            }
            if (rule.Control.Once)
            {
                _firedOnce.Add(rule.Name);
            }

            if (rule.Control.Async)
            {
                foreach (var action in rule.Actions)
                {
                    _ = Task.Run(() => actions.ExecuteAsync(action, ctx, rule.Control.Timeout));
                }
            }
            else
            {
                foreach (var action in rule.Actions)
                {
                    var result = await actions.ExecuteAsync(action, ctx, rule.Control.Timeout).ConfigureAwait(false);
                    if (rule.IsIntercept && action.Type == ActionType.PromptInject && !string.IsNullOrEmpty(result))
                    {
                        rejection = result;
                    }
                }
            }
        }

        return rejection;
    }

    /// <summary>清空 once 触发记录（新会话时）。</summary>
    public void ResetOnce() => _firedOnce.Clear();
}
