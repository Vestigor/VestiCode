namespace VestiCode.Core.Security;

/// <summary>shell 命令归一化与规则泛化：让 run_command 的会话/永久放行可复用。</summary>
public static class ShellCommand
{
    /// <summary>去掉前缀的 <c>cd &lt;path&gt; &amp;&amp;</c> / <c>cd &lt;path&gt;;</c>，得到真实命令。</summary>
    public static string StripCd(string cmd)
    {
        var s = cmd.TrimStart();
        while (s.StartsWith("cd ", StringComparison.Ordinal))
        {
            var amp = s.IndexOf("&&", StringComparison.Ordinal);
            var semi = s.IndexOf(';', StringComparison.Ordinal);
            int sep, len;
            if (amp >= 0 && (semi < 0 || amp < semi)) { sep = amp; len = 2; }
            else if (semi >= 0) { sep = semi; len = 1; }
            else { break; }
            s = s[(sep + len)..].TrimStart();
        }
        return s;
    }

    /// <summary>把命令泛化为 glob：<c>程序 [子命令] *</c>（如 <c>git log --oneline</c> → <c>git log*</c>）。</summary>
    public static string Generalize(string cmd)
    {
        var real = StripCd(cmd);
        var tokens = real.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return cmd;
        }
        var prog = tokens[0];
        var sub = tokens.Length > 1 && !tokens[1].StartsWith('-') ? tokens[1] : null;
        return sub is null ? $"{prog}*" : $"{prog} {sub}*";
    }
}

/// <summary>一条匹配工具调用的安全规则。</summary>
public sealed class SecurityRule
{
    /// <summary>工具名（支持通配，如 <c>*</c> / <c>write_*</c>）。</summary>
    public string Tool { get; set; } = "*";

    /// <summary>命中后的动作。</summary>
    public RuleAction Action { get; set; } = RuleAction.Ask;

    /// <summary>路径 glob（如 <c>*.env</c> / <c>src/**</c>）；为空表示不限路径。</summary>
    public string? PathPattern { get; set; }

    /// <summary>命令 glob；为空表示不限命令。</summary>
    public string? CommandPattern { get; set; }

    /// <summary>作用域（持久化时不写入，由加载来源决定）。</summary>
    public RuleScope Scope { get; set; } = RuleScope.Project;

    /// <summary>判断该规则是否匹配给定的工具调用。</summary>
    public bool Matches(string toolName, string? path = null, string? command = null)
    {
        if (!Glob.IsMatch(Tool, toolName))
        {
            return false;
        }
        if (PathPattern is not null)
        {
            if (path is null || !Glob.IsMatch(PathPattern, path))
            {
                return false;
            }
        }
        if (CommandPattern is not null)
        {
            // 归一化（去 cd 前缀）后再匹配，使 "cd x && git log" 命中 "git log*"。
            if (command is null || !Glob.IsMatch(CommandPattern, ShellCommand.StripCd(command)))
            {
                return false;
            }
        }
        return true;
    }
}
