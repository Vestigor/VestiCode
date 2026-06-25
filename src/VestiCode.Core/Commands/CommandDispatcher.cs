namespace VestiCode.Core.Commands;

/// <summary>输入分流：<c>/</c> 前缀走命令，否则交给 AI。</summary>
public sealed class CommandDispatcher(CommandRegistry registry, IUiControl ui)
{
    /// <summary>是否为命令输入。</summary>
    public static bool IsCommand(string text) => text.TrimStart().StartsWith('/');

    /// <summary>
    /// 分流输入。命令返回其 <see cref="CommandResult"/>；非命令返回 <c>null</c>（调用方交给 AI）。
    /// </summary>
    public async Task<CommandResult?> DispatchAsync(string text)
    {
        if (!IsCommand(text))
        {
            return null;
        }

        var (name, args) = Parse(text);
        var cmd = registry.Lookup(name);
        if (cmd is null)
        {
            return CommandResult.Show($"未知命令: /{name}\n输入 /help 查看可用命令。");
        }

        return await cmd.ExecuteAsync(args, ui).ConfigureAwait(false);
    }

    /// <summary>解析 <c>/name arg1 "arg 2" ...</c>。命令名大小写不敏感，参数支持双引号。</summary>
    public static (string Name, IReadOnlyList<string> Args) Parse(string text)
    {
        var inner = text.Trim().TrimStart('/').Trim();
        if (inner.Length == 0)
        {
            return ("", []);
        }

        var firstSpace = inner.IndexOf(' ');
        if (firstSpace < 0)
        {
            return (inner, []);
        }

        var name = inner[..firstSpace];
        var rest = inner[(firstSpace + 1)..];
        return (name, SmartSplit(rest));
    }

    /// <summary>按空白切分，保留双引号包裹的整体。</summary>
    private static List<string> SmartSplit(string text)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        foreach (var ch in text)
        {
            switch (ch)
            {
                case '"':
                    inQuotes = !inQuotes;
                    break;
                case ' ' or '\t' when !inQuotes:
                    if (current.Length > 0)
                    {
                        args.Add(current.ToString());
                        current.Clear();
                    }
                    break;
                default:
                    current.Append(ch);
                    break;
            }
        }

        if (current.Length > 0)
        {
            args.Add(current.ToString());
        }
        return args;
    }
}
