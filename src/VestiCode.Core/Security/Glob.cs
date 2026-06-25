using System.Text.RegularExpressions;

namespace VestiCode.Core.Security;

/// <summary>
/// fnmatch 风格的 glob 匹配（对应 Python <c>fnmatch.fnmatch</c>）：
/// <c>*</c> 匹配任意字符（含 <c>/</c>），<c>?</c> 匹配单个字符。区分大小写。
/// </summary>
public static class Glob
{
    public static bool IsMatch(string pattern, string value)
    {
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(value, regex, RegexOptions.CultureInvariant);
    }
}
