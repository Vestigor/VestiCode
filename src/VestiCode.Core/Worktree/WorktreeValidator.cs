using System.Text.RegularExpressions;

namespace VestiCode.Core.Worktree;

/// <summary>worktree 名称校验：严格字符集、长度、路径遍历防护。</summary>
public static partial class WorktreeValidator
{
    private const int MaxSegmentLen = 64;
    private const int MaxTotalLen = 255;

    [GeneratedRegex("^[a-zA-Z0-9_-]+$")]
    private static partial Regex SegmentRegex();

    public static (bool Valid, string Error) ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return (false, "名称不能为空");
        }
        if (name.Length > MaxTotalLen)
        {
            return (false, $"名称过长（最大 {MaxTotalLen} 字符）");
        }

        foreach (var seg in name.Split('/'))
        {
            if (seg is "" or "." or "..")
            {
                return (false, $"名称包含非法路径段: '{seg}'");
            }
            if (seg.Length > MaxSegmentLen)
            {
                return (false, $"名称段过长: '{seg}'（最大 {MaxSegmentLen} 字符）");
            }
            if (!SegmentRegex().IsMatch(seg))
            {
                return (false, $"名称包含非法字符: '{seg}'（仅允许 a-z A-Z 0-9 _ -）");
            }
        }

        return (true, "");
    }

    /// <summary>worktree 名 → git 分支名（<c>/</c>→<c>-</c>，前缀 <c>vesticode/</c>）。</summary>
    public static string NameToBranch(string name) => "vesticode/" + name.Replace('/', '-');

    /// <summary>worktree 名 → 子目录名。</summary>
    public static string NameToDirName(string name) => name.Replace('/', '-');
}
