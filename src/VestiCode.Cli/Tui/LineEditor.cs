using System.Text;

namespace VestiCode.Cli.Tui;

/// <summary>
/// 轻量行编辑器：支持普通输入、退格、回车，以及对 <c>/</c> 命令的 Tab 补全
/// （唯一匹配直接补全，多匹配列出候选）。非交互（管道）输入回退到 Console.ReadLine。
/// </summary>
public sealed class LineEditor(string prompt, Func<string, IReadOnlyList<string>> completer)
{
    /// <summary>读取一行；EOF 返回 null。</summary>
    public string? ReadLine()
    {
        if (Console.IsInputRedirected)
        {
            Console.Write(StripAnsi(prompt));
            return Console.ReadLine();
        }

        var buffer = new StringBuilder();
        Console.Write(prompt);

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return buffer.ToString();

                case ConsoleKey.Backspace:
                    if (buffer.Length > 0)
                    {
                        var removed = buffer[^1];
                        buffer.Remove(buffer.Length - 1, 1);
                        // 按显示宽度擦除：CJK 全角占 2 列，否则会删不干净留下半个字。
                        var w = CharWidth(removed);
                        Console.Write(new string('\b', w) + new string(' ', w) + new string('\b', w));
                    }
                    break;

                case ConsoleKey.Tab:
                    HandleTab(buffer);
                    break;

                case ConsoleKey.Escape:
                    buffer.Clear();
                    Redraw(buffer);
                    break;

                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        buffer.Append(key.KeyChar);
                        Console.Write(key.KeyChar);
                    }
                    break;
            }
        }
    }

    private void HandleTab(StringBuilder buffer)
    {
        var text = buffer.ToString();
        if (!text.StartsWith('/'))
        {
            return; // 仅对命令补全
        }

        var matches = completer(text);
        if (matches.Count == 0)
        {
            return;
        }

        if (matches.Count == 1)
        {
            buffer.Clear();
            buffer.Append(matches[0]).Append(' ');
            Redraw(buffer);
            return;
        }

        // 多个候选：补全到公共前缀并列出。
        var common = LongestCommonPrefix(matches);
        if (common.Length > text.Length)
        {
            buffer.Clear();
            buffer.Append(common);
        }
        Console.WriteLine();
        Console.WriteLine(string.Join("  ", matches));
        Redraw(buffer);
    }

    private void Redraw(StringBuilder buffer)
    {
        Console.Write("\r[2K" + prompt + buffer);
    }

    private static string LongestCommonPrefix(IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            return "";
        }
        var prefix = items[0];
        foreach (var item in items.Skip(1))
        {
            var i = 0;
            while (i < prefix.Length && i < item.Length && prefix[i] == item[i])
            {
                i++;
            }
            prefix = prefix[..i];
        }
        return prefix;
    }

    /// <summary>字符显示宽度：CJK 全角按 2 列计，其余 1 列。</summary>
    private static int CharWidth(char ch) =>
        ch >= 0x1100 && (
            ch <= 0x115F || (ch >= 0x2E80 && ch <= 0xA4CF) || (ch >= 0xAC00 && ch <= 0xD7A3) ||
            (ch >= 0xF900 && ch <= 0xFAFF) || (ch >= 0xFF00 && ch <= 0xFF60) || (ch >= 0xFFE0 && ch <= 0xFFE6))
            ? 2 : 1;

    private static string StripAnsi(string s)
    {
        var sb = new StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            if (s[i] == '')
            {
                while (i < s.Length && s[i] != 'm')
                {
                    i++;
                }
                i++; // skip 'm'
            }
            else
            {
                sb.Append(s[i++]);
            }
        }
        return sb.ToString();
    }
}
