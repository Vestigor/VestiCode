using System.Text;
using System.Text.RegularExpressions;

namespace VestiCode.Cli.Tui;

/// <summary>
/// 轻量 Markdown → ANSI 行级渲染器（内联流式：按完成的行渲染）。
/// 支持：代码围栏 ``` ```、标题 #、列表 -/*、引用 &gt;、表格 |a|b|、行内 **粗**/*斜*/`码`。
/// 表格需缓冲整块后对齐渲染，故 <see cref="RenderLine"/> 可能返回 0 行或多行；
/// 消息结束须调用 <see cref="Flush"/> 收尾。每条消息用一个实例（含跨行状态）。
/// </summary>
public sealed partial class MarkdownRenderer
{
    private const string Esc = "";
    private const string Reset = $"{Esc}[0m";
    private const string Base = $"{Esc}[37m";
    private const string Dim = $"{Esc}[90m";
    private const string Code = $"{Esc}[96m";
    private const string BoldW = $"{Esc}[1;97m";
    private const string Head = $"{Esc}[1;95m";

    private bool _inCodeBlock;
    private readonly List<string> _tableRows = [];

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"\*\*([^*]+)\*\*|__([^_]+)__")]
    private static partial Regex Bold();

    [GeneratedRegex(@"(?<!\*)\*([^*]+)\*(?!\*)|(?<!_)_([^_]+)_(?!_)")]
    private static partial Regex Italic();

    [GeneratedRegex(@"~~([^~]+)~~")]
    private static partial Regex Strike();

    [GeneratedRegex(@"!\[([^\]]*)\]\(([^)]+)\)")]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)]+)\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(\s*)[-*]\s+\[([ xX])\]\s+(.*)$")]
    private static partial Regex TaskItem();

    [GeneratedRegex(@"^(\s*)[-*]\s+")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^\s*\|?[\s:|-]*-[\s:|-]*\|?\s*$")]
    private static partial Regex TableSeparator();

    /// <summary>渲染一行；返回 0+ 行带 ANSI 的输出（表格会被缓冲）。</summary>
    public IReadOnlyList<string> RenderLine(string raw)
    {
        // 代码块内不参与表格判定。
        if (!_inCodeBlock && IsTableRow(raw))
        {
            _tableRows.Add(raw);
            return [];
        }

        var output = FlushTable();
        output.Add(RenderNormal(raw));
        return output;
    }

    /// <summary>消息结束：输出缓冲中的表格（或其退化行）。</summary>
    public IReadOnlyList<string> Flush() => FlushTable();

    // -- 普通行 ----------------------------------------------------------------

    private string RenderNormal(string raw)
    {
        if (raw.TrimStart().StartsWith("```", StringComparison.Ordinal))
        {
            _inCodeBlock = !_inCodeBlock;
            if (_inCodeBlock)
            {
                var lang = raw.TrimStart().TrimStart('`').Trim();
                return lang.Length > 0 ? $"{Dim}┌─ {lang} {new string('─', Math.Max(0, 40 - lang.Length))}{Reset}"
                                       : $"{Dim}┌{new string('─', 42)}{Reset}";
            }
            return $"{Dim}└{new string('─', 42)}{Reset}";
        }
        if (_inCodeBlock)
        {
            return $"{Dim}│ {Code}{raw}{Reset}";
        }

        // 水平分隔线：--- / *** / ___（3 个及以上同字符）。
        var compact = raw.Trim().Replace(" ", "");
        if (compact.Length >= 3 &&
            (compact.All(c => c == '-') || compact.All(c => c == '*') || compact.All(c => c == '_')))
        {
            return $"{Dim}{new string('─', 48)}{Reset}";
        }

        var heading = Heading().Match(raw);
        if (heading.Success)
        {
            return $"{Head}{heading.Groups[2].Value}{Reset}";
        }
        if (raw.StartsWith('>'))
        {
            return $"{Dim}│ {ApplySpans(raw.TrimStart('>', ' '))}{Reset}";
        }

        // 任务列表：- [ ] / - [x] → ☐ / ☑。
        var task = TaskItem().Match(raw);
        if (task.Success)
        {
            var box = task.Groups[2].Value == " " ? "☐" : "☑";
            return $"{Base}{task.Groups[1].Value}{box} {ApplySpans(task.Groups[3].Value)}{Reset}";
        }

        var line = ListItem().Replace(raw, "$1• ");
        return $"{Base}{ApplySpans(line)}{Reset}";
    }

    private static string ApplySpans(string s)
    {
        // 行内代码先处理，保护其内容不被后续语法二次解析。
        s = InlineCode().Replace(s, m => $"{Code}{m.Groups[1].Value}{Reset}{Base}");
        // 图片在链接之前（![alt](url) 是 [text](url) 的超集）。
        s = Image().Replace(s, m => $"{Dim}🖼 {m.Groups[1].Value}{Reset}{Base}");
        // 链接：OSC 8 终端超链接（支持的终端里可点击），文字下划线高亮。
        s = Link().Replace(s, m =>
            $"{Esc}]8;;{m.Groups[2].Value}{Esc}\\{Esc}[4;36m{m.Groups[1].Value}{Reset}{Base}{Esc}]8;;{Esc}\\");
        s = Bold().Replace(s, m => $"{BoldW}{(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)}{Reset}{Base}");
        s = Strike().Replace(s, m => $"{Esc}[9m{m.Groups[1].Value}{Reset}{Base}");
        s = Italic().Replace(s, m => $"{Esc}[3m{(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)}{Reset}{Base}");
        return s;
    }

    // -- 表格 ------------------------------------------------------------------

    private static bool IsTableRow(string raw) => raw.Contains('|') && raw.Trim().Length > 0;

    private List<string> FlushTable()
    {
        if (_tableRows.Count == 0)
        {
            return [];
        }
        var rows = new List<string>(_tableRows);
        _tableRows.Clear();

        // 合法表格：至少 表头 + 分隔行(---)。否则退化为普通行。
        if (rows.Count < 2 || !TableSeparator().IsMatch(rows[1]))
        {
            return rows.Select(RenderNormal).ToList();
        }

        var header = SplitCells(rows[0]);
        var body = rows.Skip(2).Select(SplitCells).ToList();
        var cols = header.Count;
        var widths = new int[cols];
        for (var c = 0; c < cols; c++)
        {
            widths[c] = DisplayWidth(header[c]);
            foreach (var r in body)
            {
                if (c < r.Count)
                {
                    widths[c] = Math.Max(widths[c], DisplayWidth(r[c]));
                }
            }
        }

        var output = new List<string>();
        output.Add($"{BoldW}{Join(header, widths)}{Reset}");
        output.Add($"{Dim}{Underline(widths)}{Reset}");
        foreach (var r in body)
        {
            output.Add($"{Base}{Join(r, widths)}{Reset}");
        }
        return output;
    }

    private static List<string> SplitCells(string row)
    {
        var t = row.Trim();
        if (t.StartsWith('|'))
        {
            t = t[1..];
        }
        if (t.EndsWith('|'))
        {
            t = t[..^1];
        }
        return t.Split('|').Select(c => c.Trim()).ToList();
    }

    private static string Join(IReadOnlyList<string> cells, int[] widths)
    {
        var sb = new StringBuilder();
        for (var c = 0; c < widths.Length; c++)
        {
            var cell = c < cells.Count ? cells[c] : "";
            sb.Append(ApplySpans(cell)); // 行内语法（对齐仍按原始显示宽度计算）
            sb.Append(new string(' ', Math.Max(0, widths[c] - DisplayWidth(cell)) + 2));
        }
        return sb.ToString().TrimEnd();
    }

    private static string Underline(int[] widths)
    {
        var sb = new StringBuilder();
        foreach (var w in widths)
        {
            sb.Append(new string('─', w));
            sb.Append("  ");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>显示宽度（CJK 全角按 2 计）。</summary>
    private static int DisplayWidth(string s)
    {
        var width = 0;
        foreach (var ch in s)
        {
            width += ch >= 0x1100 && (
                ch <= 0x115F || (ch >= 0x2E80 && ch <= 0xA4CF) || (ch >= 0xAC00 && ch <= 0xD7A3) ||
                (ch >= 0xF900 && ch <= 0xFAFF) || (ch >= 0xFF00 && ch <= 0xFF60) || (ch >= 0xFFE0 && ch <= 0xFFE6))
                ? 2 : 1;
        }
        return width;
    }
}
