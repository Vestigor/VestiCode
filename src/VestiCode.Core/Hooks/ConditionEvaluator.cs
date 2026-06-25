using System.Text.RegularExpressions;
using VestiCode.Core.Security;

namespace VestiCode.Core.Hooks;

/// <summary>条件评估器：exact / not / regex / glob，ALL 或 ANY 组合。</summary>
public static class ConditionEvaluator
{
    public static bool Evaluate(HookCondition? condition, IReadOnlyDictionary<string, object?> context)
    {
        if (condition is null || condition.Rules.Count == 0)
        {
            return true; // 无条件 → 始终触发
        }

        var results = condition.Rules.Select(r => EvalRule(r, context));
        return condition.Match == MatchMode.All ? results.All(x => x) : results.Any(x => x);
    }

    private static bool EvalRule(ConditionRule rule, IReadOnlyDictionary<string, object?> context)
    {
        var actual = HookContext.Resolve(rule.Field, context)?.ToString() ?? "";
        return rule.Operator switch
        {
            HookOperator.Exact => actual == rule.Value,
            HookOperator.Not => actual != rule.Value,
            HookOperator.Glob => Glob.IsMatch(rule.Value, actual),
            HookOperator.Regex => TryRegex(rule.Value, actual),
            _ => false,
        };
    }

    private static bool TryRegex(string pattern, string input)
    {
        try
        {
            return Regex.IsMatch(input, pattern);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
