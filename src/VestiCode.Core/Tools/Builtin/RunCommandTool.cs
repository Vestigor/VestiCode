using System.Text.Json.Nodes;

namespace VestiCode.Core.Tools.Builtin;

/// <summary>在<b>持久 shell 会话</b>中执行命令（cd/环境变量跨调用保持），带危险/交互式拦截与输出截断。</summary>
public sealed class RunCommandTool(PersistentShell shell) : ITool
{
    private const int OutputLimit = 10_000;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // 仅硬拦"编码工作流中绝不该出现的系统级命令"。
    // rm / chmod / chown 等正常但有风险的命令不在此列——它们交给安全门控(HITL 确认)，
    // 而真正灾难性的形式(rm -rf /、chmod 777、dd if= 等)由 CommandBlacklist 绝对拦截。
    private static readonly HashSet<string> BlockedCommands = new(StringComparer.Ordinal)
    {
        "sudo", "su", "shutdown", "reboot", "poweroff", "halt", "mkfs", "dd", ":(){",
    };

    private static readonly HashSet<string> InteractiveCommands = new(StringComparer.Ordinal)
    {
        "vim", "vi", "nano", "emacs", "ssh", "telnet", "top", "htop", "less", "more", "man",
    };

    public string Name => "run_command";

    public string Description =>
        "在持久 shell 会话中执行命令（cd 和环境变量会在多次调用间保持）。输出有长度限制；禁止交互式命令与系统级危险命令（sudo/shutdown 等），其余写操作受安全确认约束。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new ToolParameter("command", "string", "要执行的 shell 命令。"),
    ];

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, CancellationToken cancellationToken = default)
    {
        var command = arguments.GetString("command");

        // 按 shell 分隔符拆成子命令，逐段取命令头检查（堵住 "ls && rm" 这类复合命令绕过）。
        foreach (var head in CommandHeads(command))
        {
            if (BlockedCommands.Contains(head))
            {
                return ToolResult.Fail($"禁止执行危险命令: {head}");
            }
            if (InteractiveCommands.Contains(head))
            {
                return ToolResult.Fail($"禁止交互式命令: {head}");
            }
        }

        var (exitCode, output, timedOut) = await shell.ExecuteAsync(command, Timeout, cancellationToken).ConfigureAwait(false);

        if (timedOut)
        {
            return ToolResult.Fail($"命令执行超时（{Timeout.TotalSeconds}s），shell 会话已重启。", Truncate(output));
        }

        var body = Truncate(output.TrimEnd('\n'));
        var truncated = output.Length > OutputLimit;
        var header = exitCode == 0 ? "退出码: 0" : $"退出码: {exitCode}（非零）";
        var text = $"{header}\n\n{(body.Length == 0 ? "(无输出)" : body)}{(truncated ? "\n(输出已截断)" : "")}";
        return new ToolResult(exitCode == 0, text);
    }

    private static readonly string[] Separators = ["&&", "||", ";", "|", "\n"];

    /// <summary>把复合命令按 shell 分隔符拆段，取每段第一个 token（命令名）。</summary>
    private static IEnumerable<string> CommandHeads(string command)
    {
        foreach (var segment in command.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = segment.Trim();
            if (t.Length > 0)
            {
                yield return t.Split(' ', '\t')[0];
            }
        }
    }

    private static string Truncate(string s) => s.Length > OutputLimit ? s[..OutputLimit] : s;
}
