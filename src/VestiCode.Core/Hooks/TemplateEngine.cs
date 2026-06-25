using System.Text.RegularExpressions;

namespace VestiCode.Core.Hooks;

/// <summary><c>{{var}}</c> / <c>{{a.b}}</c> 模板替换，从上下文取值；未定义变量替换为空串（不抛异常）。</summary>
public static partial class TemplateEngine
{
    [GeneratedRegex(@"\{\{(\w+(?:\.\w+)*)\}\}")]
    private static partial Regex VarRegex();

    public static string Render(string template, IReadOnlyDictionary<string, object?> context)
        => VarRegex().Replace(template, m => HookContext.Resolve(m.Groups[1].Value, context)?.ToString() ?? "");
}

/// <summary>点路径取值的共享工具（条件评估与模板替换共用）。</summary>
public static class HookContext
{
    /// <summary>解析点路径 <paramref name="field"/>（如 <c>params.command</c>）到嵌套字典的值。</summary>
    public static object? Resolve(string field, IReadOnlyDictionary<string, object?> context)
    {
        object? current = context;
        foreach (var part in field.Split('.'))
        {
            if (current is IReadOnlyDictionary<string, object?> dict && dict.TryGetValue(part, out var next))
            {
                current = next;
            }
            else
            {
                return "";
            }
        }
        return current ?? "";
    }
}
