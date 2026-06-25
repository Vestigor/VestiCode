using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Spectre.Console;
using VestiCode.Core.Agents;
using VestiCode.Core.Commands;
using VestiCode.Core.Configuration;
using VestiCode.Core.Conversation;
using VestiCode.Core.Mcp;
using VestiCode.Core.Memory;
using VestiCode.Core.Notes;
using VestiCode.Core.Security;
using VestiCode.Core.Skills;
using VestiCode.Core.Tools;

namespace VestiCode.Cli.Tui;

/// <summary>
/// 内联终端 UI（普通滚动缓冲，支持终端原生选中复制；像 Claude Code）：
/// 顶部图标头 → ❯ 用户 / ● 步骤(│ 串联) / markdown 渲染的流式正文 / ● 工具(绿红) →
/// 底部单行 ✻ 活动状态行（任务结束消失）。
/// </summary>
public sealed class ConsoleApp : IUiControl
{
    private const string Esc = "";
    private const string Reset = $"{Esc}[0m";
    private const string Green = $"{Esc}[92m";
    private const string Red = $"{Esc}[91m";
    private const string Gray = $"{Esc}[90m";
    private const string Prompt = $"{Esc}[92m❯{Esc}[0m ";

    private readonly AgentLoop _agentLoop;
    private readonly VestiCode.Core.Teams.DispatchScheduler _dispatch;
    private readonly JsonlSessionStore _sessions;
    private readonly ContextCompressor _compressor;
    private readonly SecurityGuard _guard;
    private readonly CommandRegistry _registry;
    private readonly SkillRegistry _skills;
    private readonly AutoNoteManager _notes;
    private readonly ConversationHistory _history;
    private readonly ToolRegistry _tools;
    private readonly McpManager _mcp;
    private readonly ProviderOptions _active;
    private readonly CommandDispatcher _dispatcher;
    private readonly StatusIndicator _status = new();
    private readonly LineEditor _editor;
    private readonly Dictionary<string, DateTime> _toolStart = new(StringComparer.Ordinal);

    private CancellationTokenSource? _runCts;
    private int _turnSteps;

    public ConsoleApp(
        AgentLoop agentLoop, JsonlSessionStore sessions, ContextCompressor compressor, SecurityGuard guard,
        CommandRegistry registry, SkillRegistry skills, AutoNoteManager notes, ConversationHistory history,
        ToolRegistry tools, McpManager mcp, IOptions<AppOptions> options,
        VestiCode.Core.Teams.DispatchScheduler dispatch)
    {
        _agentLoop = agentLoop;
        _dispatch = dispatch;
        _sessions = sessions;
        _compressor = compressor;
        _guard = guard;
        _registry = registry;
        _skills = skills;
        _notes = notes;
        _history = history;
        _tools = tools;
        _mcp = mcp;
        _active = options.Value.Providers.First(p => p.Name == options.Value.ActiveProvider);
        _dispatcher = new CommandDispatcher(_registry, this);
        _editor = new LineEditor(Prompt, t => _registry.GetCompletions(t));
    }

    public async Task RunAsync()
    {
        PrintBanner();
        Console.CancelKeyPress += OnCancelKeyPress;

        while (true)
        {
            var input = _editor.ReadLine();
            if (input is null)
            {
                break;
            }
            input = input.Trim();
            if (input.Length == 0)
            {
                continue;
            }
            if (input is "/exit" or "/quit")
            {
                break;
            }

            if (CommandDispatcher.IsCommand(input))
            {
                await HandleCommandAsync(input).ConfigureAwait(false);
            }
            else
            {
                // 用户输入行已由 LineEditor 在终端回显，无需重复打印。
                _history.AddUserMessage(input);
                await RunTurnAsync(input).ConfigureAwait(false);
            }
        }

        Console.CancelKeyPress -= OnCancelKeyPress;
        AnsiConsole.MarkupLine("[dim]Goodbye![/]");
        await FinalizeNotesAsync().ConfigureAwait(false);
    }

    private void PrintBanner()
    {
        var mcpInfo = _mcp.ConnectedServers.Count > 0
            ? $"Connected to {_mcp.ConnectedServers.Count} MCP server(s), {_tools.Tools.Count} tools registered"
            : $"{_tools.Tools.Count} tools registered";

        // 猫咪图标 + 右侧信息。
        var cat = new[]
        {
            @"   /\___/\",
            @"  /  o o  \",
            @" (    ^    )",
            @"  \ \___/ /",
            @"   \/___\/",
        };
        var info = new[]
        {
            "[bold magenta]VestiCode[/] [dim]v0.1.0[/]",
            $"[dim]{Markup.Escape($"{_active.Name} ({_active.Model})")}[/]",
            $"[dim]{Markup.Escape(Directory.GetCurrentDirectory())}[/]",
            $"[dim]{Markup.Escape(mcpInfo)}[/]",
            "[dim]THINK · CODE · ACT   ·   /help 命令 · esc 中断[/]",
        };
        for (var i = 0; i < cat.Length; i++)
        {
            AnsiConsole.MarkupLine($"[magenta]{cat[i].PadRight(13)}[/]  {info[i]}");
        }
        AnsiConsole.WriteLine();
    }

    private async Task HandleCommandAsync(string input)
    {
        var result = await _dispatcher.DispatchAsync(input).ConfigureAwait(false);
        if (result is null)
        {
            return;
        }
        if (result.Message is not null)
        {
            AnsiConsole.MarkupLine($"[dim]{Markup.Escape(result.Message)}[/]");
            AnsiConsole.WriteLine();
        }
        if (result.InjectPrompt is not null)
        {
            _history.AddUserMessage(result.InjectPrompt);
            await RunTurnAsync(result.InjectPrompt).ConfigureAwait(false);
        }
    }

    private async Task RunTurnAsync(string userMessage)
    {
        _runCts = new CancellationTokenSource();
        using var watcherCts = new CancellationTokenSource();
        var watcher = StartEscapeWatcher(watcherCts.Token);
        _turnSteps = 0;
        var assistantText = "";
        Console.WriteLine(); // 用户输入与 AI 回复之间空一行
        _status.Start();
        try
        {
            assistantText = await RunAgentAsync(_runCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _status.WriteAbove($"{Gray}Interrupted{Reset}");
        }
        finally
        {
            await _status.StopAsync().ConfigureAwait(false);
            await watcherCts.CancelAsync().ConfigureAwait(false);
            try { await watcher.ConfigureAwait(false); } catch (OperationCanceledException) { }
            _runCts?.Dispose();
            _runCts = null;
            _sessions.Save(_history, _active.Name, _active.Model);
        }

        _notes.RecordRound(userMessage, assistantText);
        if (_notes.ShouldUpdate())
        {
            _ = _notes.UpdateAllAsync();
        }
        AnsiConsole.WriteLine();
    }

    private async Task<string> RunAgentAsync(CancellationToken cancellationToken)
    {
        var assistantText = new StringBuilder();
        var lineBuffer = new StringBuilder();
        var md = new MarkdownRenderer();
        var thinking = false;
        var assistantStarted = false;

        // 步骤间用空行分隔（首步不加）。
        void Connector()
        {
            if (_turnSteps++ > 0)
            {
                _status.WriteAbove("");
            }
        }

        void FinalizeThinking()
        {
            if (thinking)
            {
                // 仅在确有耗时（≥2s）时显示思考步骤，避免每次回复都刷 "Thought for 0s/1s" 噪音。
                if (_status.ElapsedSeconds >= 2)
                {
                    Connector();
                    _status.WriteAbove($"{Gray}● Thought for {_status.ElapsedSeconds:0}s{Reset}");
                }
                thinking = false;
                _status.SetStatus();
            }
        }

        // 首行带 ● 标记，续行缩进 2 格对齐到内容之下。
        void Emit(string renderedLine)
        {
            if (!assistantStarted)
            {
                Connector();
                _status.WriteAbove($"{Gray}●{Reset} {renderedLine}");
                assistantStarted = true;
            }
            else
            {
                _status.WriteAbove($"  {renderedLine}");
            }
        }

        void CommitAssistantRaw(string rawLine)
        {
            foreach (var outLine in md.RenderLine(rawLine))
            {
                Emit(outLine);
            }
        }

        void FlushAssistant(bool final)
        {
            var text = lineBuffer.ToString();
            int idx;
            while ((idx = text.IndexOf('\n')) >= 0)
            {
                CommitAssistantRaw(text[..idx]);
                text = text[(idx + 1)..];
            }
            lineBuffer.Clear();
            lineBuffer.Append(text);
            if (final)
            {
                if (lineBuffer.Length > 0)
                {
                    CommitAssistantRaw(lineBuffer.ToString());
                    lineBuffer.Clear();
                }
                foreach (var outLine in md.Flush()) // 收尾：输出缓冲中的表格
                {
                    Emit(outLine);
                }
            }
        }

        await foreach (var ev in _agentLoop.RunAsync(_history, cancellationToken).ConfigureAwait(false))
        {
            switch (ev)
            {
                case RoundStartEvent:
                    _status.SetThinking();
                    thinking = true;
                    assistantStarted = false;
                    md = new MarkdownRenderer();
                    break;

                case TextDeltaEvent text:
                    FinalizeThinking();
                    assistantText.Append(text.Text);
                    lineBuffer.Append(text.Text);
                    FlushAssistant(final: false);
                    break;

                case ToolCallEvent call:
                    FinalizeThinking();
                    FlushAssistant(final: true);
                    assistantStarted = false;
                    _status.SetStatus();
                    _toolStart[call.Call.Name] = DateTime.UtcNow;
                    break;

                case ToolResultEvent res:
                    var dur = _toolStart.TryGetValue(res.ToolName, out var st) ? (DateTime.UtcNow - st).TotalSeconds : 0;
                    Connector();
                    var color = res.Result.Success ? Green : Red;
                    var a = ArgSummary(res.Arguments);
                    var arg = a.Length > 0 ? $" {Gray}{a}{Reset}" : "";
                    _status.WriteAbove($"{color}● {res.ToolName}{arg}{color} ({dur:0.0}s){Reset}");
                    var preview = PreviewLines(res, 3);
                    for (var i = 0; i < preview.Count; i++)
                    {
                        _status.WriteAbove($"{Gray}  {(i == 0 ? "⎿" : " ")} {preview[i]}{Reset}");
                    }
                    break;

                case ToolBlockedEvent blocked:
                    Connector();
                    _status.WriteAbove($"{Red}● {blocked.ToolName} 已拦截{Reset}");
                    _status.WriteAbove($"{Gray}  ⎿ {blocked.Reason}{Reset}");
                    break;

                case HitlRequestEvent hitl:
                    _status.Suspend();
                    hitl.Decision.SetResult(PromptHitl(hitl));
                    _status.Resume();
                    _toolStart[hitl.ToolName] = DateTime.UtcNow; // 确认后才开始计执行时间
                    break;

                case ContextWarningEvent w:
                    Connector();
                    _status.WriteAbove($"{Gray}● 上下文窗口使用超过 {w.EstimatedTokens} tokens（窗口 {w.ContextWindow}，约 {(w.ContextWindow > 0 ? w.EstimatedTokens * 100 / w.ContextWindow : 0)}%），接近上限将自动压缩{Reset}");
                    break;

                case CompactionEvent c:
                    Connector();
                    _status.WriteAbove($"{Gray}● 已压缩上下文：合并 {c.MessagesCompressed} 条，省 ~{c.TokensSaved} tokens{Reset}");
                    break;

                case ErrorEvent err:
                    FinalizeThinking();
                    FlushAssistant(final: true);
                    Connector();
                    _status.WriteAbove($"{Red}● 错误：{err.Message}{Reset}");
                    // 鉴权类错误：服务器只回打码的 key，这里补上本机实际发出的 Key（脱敏）便于排查。
                    if (err.Message.Contains("401") ||
                        err.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
                        err.Message.Contains("api key", StringComparison.OrdinalIgnoreCase))
                    {
                        _status.WriteAbove(
                            $"{Gray}  ⎿ 本机发出的 Key：{SetupWizard.Mask(_active.ApiKey)}（Provider: {_active.Name}）{Reset}");
                    }
                    break;

                case AgentDoneEvent done:
                    FinalizeThinking();
                    FlushAssistant(final: true);
                    if (done.Reason == AgentDoneReason.MaxRounds)
                    {
                        Connector();
                        _status.WriteAbove($"{Gray}● 已达到最大轮次限制{Reset}");
                    }
                    break;
            }
        }

        return assistantText.ToString();
    }

    /// <summary>提取工具调用的关键参数用于步骤行展示（命令/路径/模式/URL），压成单行并截断。</summary>
    private static string ArgSummary(JsonObject args)
    {
        var v = args["command"]?.ToString()
            ?? args["path"]?.ToString()
            ?? args["pattern"]?.ToString()
            ?? args["uri"]?.ToString()
            ?? args["url"]?.ToString()
            ?? args["name"]?.ToString()
            ?? "";
        v = string.Join(' ', v.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return v.Length > 70 ? v[..70] + "…" : v;
    }

    /// <summary>工具结果预览：取前 <paramref name="max"/> 行非空内容（每行截断），多余行数附在末行。</summary>
    private static IReadOnlyList<string> PreviewLines(ToolResultEvent ev, int max)
    {
        // 优先取 Content（run_command 失败时退出码等信息在 Content 里），为空再回退 Error。
        var raw = ev.Result.Content.Length > 0 ? ev.Result.Content : ev.Result.Error;
        var nonEmpty = raw.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => l.Trim().Length > 0)
            .ToList();
        if (nonEmpty.Count == 0)
        {
            return ["(无输出)"];
        }
        var shown = nonEmpty.Take(max)
            .Select(l => l.Length > 120 ? l[..120] + "…" : l)
            .ToList();
        var remaining = nonEmpty.Count - shown.Count;
        if (remaining > 0)
        {
            shown[^1] += $"  (+{remaining} 行)";
        }
        return shown;
    }

    private HitlVerdict PromptHitl(HitlRequestEvent hitl)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(hitl.Prompt)}[/]");
        if (Console.IsInputRedirected)
        {
            AnsiConsole.MarkupLine("[dim](非交互模式：自动允许本次)[/]");
            return new HitlVerdict(HitlDecision.AllowOnce);
        }
        while (true)
        {
            switch (Console.ReadKey(intercept: true).Key)
            {
                case ConsoleKey.A: return new HitlVerdict(HitlDecision.AllowOnce);
                case ConsoleKey.S: return new HitlVerdict(HitlDecision.AllowSession);
                case ConsoleKey.P: return new HitlVerdict(HitlDecision.AllowPermanent);
                case ConsoleKey.D:
                    // 拒绝时可附一句原因，回灌给模型让它换做法（回车跳过）。
                    AnsiConsole.Markup("[dim]  拒绝原因（可选，回车跳过）：[/]");
                    var reason = Console.ReadLine()?.Trim() ?? "";
                    return new HitlVerdict(HitlDecision.Deny, reason);
            }
        }
    }

    private Task StartEscapeWatcher(CancellationToken stop)
    {
        if (Console.IsInputRedirected)
        {
            return Task.CompletedTask;
        }
        return Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Escape)
                {
                    _runCts?.Cancel();
                    break;
                }
                try { await Task.Delay(50, stop).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }, stop);
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        if (_runCts is { IsCancellationRequested: false })
        {
            e.Cancel = true;
            _runCts.Cancel();
        }
    }

    private async Task FinalizeNotesAsync()
    {
        if (!_notes.HasPending)
        {
            return;
        }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _notes.UpdateAllAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception) { }
    }

    // -- IUiControl ------------------------------------------------------------

    public IReadOnlyList<CommandInfo> ListCommands() =>
        _registry.ListVisible().Select(c => new CommandInfo(c.Name, c.Description, c.Usage)).ToList();

    public void WriteNotice(string message) =>
        AnsiConsole.MarkupLine($"[dim]  · {Markup.Escape(message)}[/]");

    public bool TogglePlanMode() => _agentLoop.TogglePlanOnly();

    public bool GetPlanOnly() => _agentLoop.PlanOnly;

    public bool ToggleDispatchMode()
    {
        _dispatch.SetLock1(!_dispatch.Lock1);
        return _dispatch.IsActive;
    }

    public string SetSecurityLevel(string levelName)
    {
        if (!Enum.TryParse<SecurityLevel>(levelName, ignoreCase: true, out var level))
        {
            return $"未知档位: {levelName}（可选 strict / normal / permissive）";
        }
        _agentLoop.SetSecurityLevel(level);
        return $"已切换安全档位到 {level}";
    }

    public string GetSecurityLevel() => _guard.Policy.Level.ToString();

    public int GetTokenCount() => _history.EstimatedTokenCount();

    public string GetProviderLabel() => $"{_active.Name} ({_active.Model})";

    public string GetApiKeyMasked() => SetupWizard.Mask(_active.ApiKey);

    public void ClearConversation()
    {
        _history.Clear();
        _skills.ClearActivated();
    }

    public async Task<string> TriggerCompressAsync()
    {
        _compressor.Reset(); // 手动压缩：复位熔断器，让因连续失败而停摆的压缩可重新尝试
        var result = await _compressor.CheckAndCompressAsync(_history).ConfigureAwait(false);
        return result.WasCompressed
            ? $"已压缩：合并 {result.MessagesCompressed} 条消息，约省 {result.EstimatedTokensSaved} tokens。"
            : "当前上下文未达到压缩阈值，无需压缩。";
    }

    public IReadOnlyList<SessionInfo> GetSessionList() => _sessions.ListSessions();

    public string LoadSession(string sessionId)
    {
        var loaded = _sessions.Load(sessionId);
        if (loaded is null)
        {
            return $"未找到会话: {sessionId}";
        }
        _history.ReplaceMessages(loaded.Value.History.GetMessages());
        return $"已加载会话 {sessionId}（{_history.Count} 条消息）。";
    }

    public string NewSession()
    {
        _history.Clear();
        var id = _sessions.NewSession();
        return $"已开始新会话 {id}。";
    }

    public string DeleteSession(string sessionId) =>
        _sessions.Delete(sessionId) ? $"已删除会话 {sessionId}。" : $"未找到会话: {sessionId}";
}
