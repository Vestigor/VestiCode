using System.Text.Json.Nodes;

namespace VestiCode.Core.Tools;

/// <summary>从工具调用的 <see cref="JsonObject"/> 参数中安全读取值的扩展方法。</summary>
public static class ToolArguments
{
    /// <summary>读取必填字符串参数；缺失时抛 <see cref="ArgumentException"/>（由执行器捕获为失败结果）。</summary>
    public static string GetString(this JsonObject args, string key)
    {
        if (args.TryGetPropertyValue(key, out var node) && node is not null)
        {
            return node.GetValue<string>();
        }
        throw new ArgumentException($"缺少必填参数: {key}");
    }

    /// <summary>读取可选字符串参数，缺失返回 <paramref name="fallback"/>。</summary>
    public static string? GetStringOrDefault(this JsonObject args, string key, string? fallback = null)
        => args.TryGetPropertyValue(key, out var node) && node is not null
            ? node.GetValue<string>()
            : fallback;

    /// <summary>读取可选布尔参数。</summary>
    public static bool GetBoolOrDefault(this JsonObject args, string key, bool fallback = false)
        => args.TryGetPropertyValue(key, out var node) && node is not null
            ? node.GetValue<bool>()
            : fallback;

    /// <summary>读取可选整数参数。</summary>
    public static int GetIntOrDefault(this JsonObject args, string key, int fallback = 0)
        => args.TryGetPropertyValue(key, out var node) && node is not null
            ? node.GetValue<int>()
            : fallback;
}
