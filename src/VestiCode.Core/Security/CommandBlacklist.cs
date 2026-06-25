using System.Text.RegularExpressions;

namespace VestiCode.Core.Security;

/// <summary>
/// 硬编码的危险命令黑名单——始终生效，不受权限档位影响，且不能被规则覆盖。
/// </summary>
public static class CommandBlacklist
{
    private static readonly string[] BlockedPrefixes =
    [
        "rm -rf /", "rm -rf ~", "rm -rf .", "sudo rm", "chmod 777", "chmod -R 777",
        "chown -R", "mkfs.", "dd if=", ":(){ :|:& };:", "> /dev/sda",
    ];

    private static readonly Regex[] BlockedPatterns =
    [
        new(@"curl\s+.*\|\s*(ba)?sh", RegexOptions.Compiled),
        new(@"wget\s+.*-O\s*-\s*\|\s*(ba)?sh", RegexOptions.Compiled),
        new(@"eval\s+", RegexOptions.Compiled),
        new(@">\s*/dev/[hs]d[a-z]", RegexOptions.Compiled),
        new(@"\bmv\s+.*\s+/dev/null\b", RegexOptions.Compiled),
    ];

    // shell 复合命令分隔符：逐段检查，防止 "ls && rm -rf /" 绕过前缀匹配。
    private static readonly string[] Separators = ["&&", "||", ";", "|", "\n"];

    /// <summary>检查命令；被拦截返回原因，安全返回 <c>null</c>。</summary>
    public static string? Check(string command)
    {
        var stripped = command.Trim();

        // 正则模式对整条命令匹配（本就可匹配任意位置）。
        foreach (var pattern in BlockedPatterns)
        {
            if (pattern.IsMatch(stripped))
            {
                return $"命令被黑名单拦截（匹配模式: {pattern}）";
            }
        }

        // 前缀按 shell 分隔符拆成子命令逐段匹配（堵住复合命令绕过）。
        foreach (var segment in stripped.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var lower = segment.Trim().ToLowerInvariant();
            foreach (var prefix in BlockedPrefixes)
            {
                if (lower.StartsWith(prefix.ToLowerInvariant(), StringComparison.Ordinal))
                {
                    return $"命令被黑名单拦截（匹配: {prefix}）";
                }
            }
        }

        return null;
    }
}
