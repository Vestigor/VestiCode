using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VestiCode.Core.Configuration;
using VestiCode.Core.Conversation;
using VestiCode.Core.Hooks;
using VestiCode.Core.Llm;
using VestiCode.Core.Prompts;
using VestiCode.Core.Security;
using VestiCode.Core.Skills;
using VestiCode.Core.Tools;

namespace VestiCode.Core.Agents;

/// <summary>
/// ReAct 推理循环：调 LLM → 解析工具调用 → 分批执行（读并发 / 写串行）→ 结果回填 → 继续，
/// 直到模型不再请求工具，或达到最大轮次。这是 Agent 区别于普通 LLM 对话的核心。
/// </summary>
public sealed class AgentLoop
{
    private readonly ILlmProvider _provider;
    private readonly ToolRegistry _toolRegistry;
    private readonly ToolExecutor _toolExecutor;
    private readonly SecurityGuard? _securityGuard;
    private readonly ContextCompressor? _compressor;
    private readonly PromptBuilder? _promptBuilder;
    private readonly PromptInjector? _promptInjector;
    private readonly SkillRegistry? _skillRegistry;
    private readonly HookEngine? _hookEngine;
    private readonly Teams.DispatchScheduler? _dispatch;
    private readonly ILogger<AgentLoop> _logger;
    private readonly int _maxRounds;

    // plan-only 模式下仅允许的只读工具。
    private static readonly HashSet<string> PlanModeAllowed = new(StringComparer.Ordinal)
    {
        "read_file", "glob", "grep",
    };

    private string _systemPrompt = DefaultSystemPrompt;
    private bool _planOnly;

    public AgentLoop(
        ILlmProvider provider,
        ToolRegistry toolRegistry,
        ToolExecutor toolExecutor,
        IOptions<AppOptions> options,
        ILogger<AgentLoop> logger,
        SecurityGuard? securityGuard = null,
        ContextCompressor? compressor = null,
        PromptBuilder? promptBuilder = null,
        PromptInjector? promptInjector = null,
        SkillRegistry? skillRegistry = null,
        HookEngine? hookEngine = null,
        Teams.DispatchScheduler? dispatch = null)
    {
        _provider = provider;
        _toolRegistry = toolRegistry;
        _toolExecutor = toolExecutor;
        _securityGuard = securityGuard;
        _compressor = compressor;
        _promptBuilder = promptBuilder;
        _promptInjector = promptInjector;
        _skillRegistry = skillRegistry;
        _hookEngine = hookEngine;
        _dispatch = dispatch;
        _logger = logger;
        _maxRounds = options.Value.Agent.MaxRounds;
    }

    /// <summary>设置 System Prompt（Phase 2 起改由 PromptBuilder 动态拼装）。</summary>
    public void SetSystemPrompt(string prompt) => _systemPrompt = prompt;

    /// <summary>切换全局权限档位。</summary>
    public void SetSecurityLevel(SecurityLevel level) => _securityGuard?.SetLevel(level);

    /// <summary>plan-only 模式：仅允许只读工具，写操作被拦截。</summary>
    public bool PlanOnly => _planOnly;

    /// <summary>切换 plan-only 模式，返回新状态。</summary>
    public bool TogglePlanOnly()
    {
        _planOnly = !_planOnly;
        _promptInjector?.SetPlanOnly(_planOnly);
        return _planOnly;
    }

    /// <summary>
    /// 运行一次完整的 ReAct 循环，以事件流形式产出过程。
    /// 取消语义：在轮次边界取消会产出 <see cref="AgentDoneEvent"/>(Cancelled)；
    /// 流式或工具执行中途取消则抛 <see cref="OperationCanceledException"/>，由调用方渲染 Interrupted。
    /// </summary>
    public async IAsyncEnumerable<AgentEvent> RunAsync(
        ConversationHistory history,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var round = 1; round <= _maxRounds; round++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                yield return new AgentDoneEvent(AgentDoneReason.Cancelled);
                yield break;
            }

            // 层2：接近上下文窗口时，先用结构化摘要压缩历史。
            if (_compressor is not null)
            {
                var compression = await _compressor.CheckAndCompressAsync(history, cancellationToken)
                    .ConfigureAwait(false);
                if (compression.WarningIssued)
                {
                    yield return new ContextWarningEvent(compression.EstimatedTokens, _compressor.ContextWindow);
                }
                if (compression.WasCompressed)
                {
                    yield return new CompactionEvent(compression.MessagesCompressed, compression.EstimatedTokensSaved);
                }
            }

            yield return new RoundStartEvent(round, _maxRounds);

            // Hook: ROUND_START
            if (_hookEngine is { HasRules: true })
            {
                await _hookEngine.FireAsync(HookEvent.RoundStart, new Dictionary<string, object?>
                {
                    ["round_number"] = round,
                    ["max_rounds"] = _maxRounds,
                }).ConfigureAwait(false);
            }

            // 1. 拼装本轮发送给模型的消息（System Prompt + 历史 + 每轮注入）。
            var messages = AssembleMessages(history, round);
            var toolDefs = BuildToolDefs();

            // 2. 调 LLM，边流式产出边累积本轮的文本与工具调用。
            var textBuffer = new StringBuilder();
            var toolCalls = new List<ToolCall>();
            var hadError = false;

            await foreach (var item in _provider
                .ChatStreamAsync(messages, toolDefs, cancellationToken)
                .ConfigureAwait(false))
            {
                switch (item)
                {
                    case TextDelta td:
                        textBuffer.Append(td.Text);
                        yield return new TextDeltaEvent(td.Text);
                        break;
                    case ThinkingDelta th:
                        yield return new ThinkingEvent(th.Text, th.Label);
                        break;
                    case ToolCallReady tc:
                        toolCalls.Add(tc.Call);
                        yield return new ToolCallEvent(tc.Call);
                        break;
                    case UsageReport ur:
                        yield return new UsageEvent(ur.InputTokens, ur.OutputTokens);
                        break;
                    case StreamError se:
                        _logger.LogError("LLM 流式错误 ({Status}): {Message}", se.StatusCode, se.Message);
                        yield return new ErrorEvent(
                            se.StatusCode is { } s ? $"[{s}] {se.Message}" : se.Message);
                        hadError = true;
                        break;
                }

                if (hadError)
                {
                    yield break;
                }
            }

            // Hook: MESSAGE_POST_RECEIVE
            if (_hookEngine is { HasRules: true })
            {
                var text = textBuffer.ToString();
                await _hookEngine.FireAsync(HookEvent.MessagePostReceive, new Dictionary<string, object?>
                {
                    ["text"] = text.Length > 500 ? text[..500] : text,
                    ["tool_calls_count"] = toolCalls.Count.ToString(),
                }).ConfigureAwait(false);
            }

            // 3. 模型不再请求工具 → 任务完成。
            if (toolCalls.Count == 0)
            {
                if (textBuffer.Length > 0)
                {
                    history.AddAssistantMessage(textBuffer.ToString());
                }
                yield return new AgentDoneEvent(AgentDoneReason.NoToolCall);
                yield break;
            }

            // 4. 把（可能的）前置文本与工具调用合并为一条 assistant 消息写入历史。
            history.AddRawMessage(ChatMessage.FromToolCalls(toolCalls, textBuffer.ToString()));

            // 5. 分批执行：读类并发，写类串行；执行前逐个过安全门控（可能触发 HITL）。
            var (reads, writes) = PartitionByCategory(toolCalls);

            // 读类：先逐个门控，通过的收集起来再并发执行。
            var allowedReads = new List<ToolCall>();
            foreach (var call in reads)
            {
                var gate = GateTool(call);
                if (gate.Decision == SecurityDecision.Ask)
                {
                    var tcs = NewHitlSource();
                    yield return new HitlRequestEvent(
                        call.Name, call.Arguments, _securityGuard!.BuildHitlPrompt(call.Name, call.Arguments), tcs);
                    var verdict = await tcs.Task.ConfigureAwait(false);
                    if (verdict.Decision == HitlDecision.Deny)
                    {
                        yield return DenyTool(history, call, verdict.Reason);
                        continue;
                    }
                    _securityGuard.ApplyHitl(verdict.Decision, call.Name, call.Arguments);
                }
                else if (gate.Decision == SecurityDecision.Deny)
                {
                    yield return BlockTool(history, call, gate.Reason);
                    continue;
                }
                allowedReads.Add(call);
            }

            if (allowedReads.Count > 0)
            {
                var results = await ExecuteConcurrentAsync(allowedReads, cancellationToken).ConfigureAwait(false);
                for (var i = 0; i < allowedReads.Count; i++)
                {
                    yield return new ToolResultEvent(allowedReads[i].Name, allowedReads[i].Arguments, results[i]);
                    AppendToolResult(history, allowedReads[i], results[i]);
                }
            }

            // 写类：逐个门控并串行执行。
            foreach (var call in writes)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    yield return new AgentDoneEvent(AgentDoneReason.Cancelled);
                    yield break;
                }

                // plan-only 模式：写操作一律拦截。
                if (_planOnly && !PlanModeAllowed.Contains(call.Name))
                {
                    yield return BlockTool(history, call, "plan-only 模式已开启，写入类工具被拦截。请先关闭 plan-only 再执行修改。");
                    continue;
                }

                var gate = GateTool(call);
                if (gate.Decision == SecurityDecision.Ask)
                {
                    var tcs = NewHitlSource();
                    yield return new HitlRequestEvent(
                        call.Name, call.Arguments, _securityGuard!.BuildHitlPrompt(call.Name, call.Arguments), tcs);
                    var verdict = await tcs.Task.ConfigureAwait(false);
                    if (verdict.Decision == HitlDecision.Deny)
                    {
                        yield return DenyTool(history, call, verdict.Reason);
                        continue;
                    }
                    _securityGuard.ApplyHitl(verdict.Decision, call.Name, call.Arguments);
                }
                else if (gate.Decision == SecurityDecision.Deny)
                {
                    yield return BlockTool(history, call, gate.Reason);
                    continue;
                }

                // Hook: TOOL_PRE_EXEC（拦截）——返回拒绝原因则阻止执行并回灌。
                if (_hookEngine is { HasRules: true })
                {
                    var reject = await _hookEngine
                        .FireAsync(HookEvent.ToolPreExec, ToolContext(call))
                        .ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(reject))
                    {
                        yield return BlockTool(history, call, reject);
                        continue;
                    }
                }

                var result = await ExecuteSingleAsync(call, cancellationToken).ConfigureAwait(false);
                yield return new ToolResultEvent(call.Name, call.Arguments, result);
                AppendToolResult(history, call, result);

                // Hook: TOOL_POST_EXEC
                if (_hookEngine is { HasRules: true })
                {
                    var ctx = ToolContext(call);
                    ctx["success"] = result.Success.ToString();
                    await _hookEngine.FireAsync(HookEvent.ToolPostExec, ctx).ConfigureAwait(false);
                }
            }
        }

        yield return new AgentDoneEvent(AgentDoneReason.MaxRounds);
    }

    // -- 内部 ------------------------------------------------------------------

    /// <summary>把工具调用转为 Hook 上下文：<c>tool_name</c> + 嵌套 <c>params</c>。</summary>
    private static Dictionary<string, object?> ToolContext(ToolCall call)
    {
        var prms = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in call.Arguments)
        {
            prms[key] = value?.ToString() ?? "";
        }
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tool_name"] = call.Name,
            ["params"] = prms,
        };
    }

    /// <summary>构造本轮工具定义，应用已激活 Skill 的工具白名单（skill_loader 始终可用）。</summary>
    private IReadOnlyList<ToolDefinition> BuildToolDefs()
    {
        var all = _toolRegistry.GetDefinitions();
        var whitelist = _skillRegistry?.GetActiveToolWhitelist();
        if (whitelist is not null)
        {
            all = all.Where(t => whitelist.Contains(t.Name) || t.Name == "skill_loader").ToList();
        }
        // 双锁调度模式激活时：剥夺读写/执行类工具，迫使其只能委派（sub_agent/team）。
        return _dispatch is { IsActive: true } ? _dispatch.FilterTools(all) : all;
    }

    /// <summary>对单个工具调用执行安全门控。无 guard 时一律放行。</summary>
    private SecurityCheck GateTool(ToolCall call) =>
        _securityGuard?.Check(call.Name, call.Arguments) ?? new SecurityCheck(SecurityDecision.Allow);

    /// <summary>用户拒绝：把（可选的）拒绝原因回灌历史，促使模型换一种做法。</summary>
    private static ToolResultEvent DenyTool(ConversationHistory history, ToolCall call, string reason)
    {
        var msg = string.IsNullOrWhiteSpace(reason)
            ? "用户拒绝了该操作。"
            : $"用户拒绝了该操作。原因：{reason}";
        var denied = ToolResult.Fail(msg);
        AppendToolResult(history, call, denied);
        return new ToolResultEvent(call.Name, call.Arguments, denied);
    }

    /// <summary>安全策略拦截：把原因回灌历史并产出 Blocked 事件。</summary>
    private static ToolBlockedEvent BlockTool(ConversationHistory history, ToolCall call, string reason)
    {
        AppendToolResult(history, call, ToolResult.Fail(reason));
        return new ToolBlockedEvent(call.Name, reason);
    }

    // RunContinuationsAsynchronously：避免设置结果时同步回到 UI 线程造成重入。
    private static TaskCompletionSource<HitlVerdict> NewHitlSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private List<ChatMessage> AssembleMessages(ConversationHistory history, int round)
    {
        var result = new List<ChatMessage>();
        // 优先用模块化 PromptBuilder（含实时环境信息）；无则回退到内置默认提示。
        var systemText = _promptBuilder?.BuildWithEnvironment() ?? _systemPrompt;
        if (!string.IsNullOrEmpty(systemText))
        {
            result.Add(ChatMessage.FromSystem(systemText));
        }
        // 双锁调度模式：注入 10 阶段指挥官工作流，明确"只委派、不亲自动手"。
        if (_dispatch is { IsActive: true } d && d.GetWorkflowInstructions() is { Length: > 0 } wf)
        {
            result.Add(ChatMessage.FromSystem(wf));
        }
        // 已激活 Skill 的 SOP 指令（固定注入，始终可见）。
        var skillText = _skillRegistry?.GetActiveInstructions();
        if (!string.IsNullOrEmpty(skillText))
        {
            result.Add(ChatMessage.FromSystem($"[Activated Skills]\n{skillText}"));
        }

        result.AddRange(history.GetMessages());

        // 每轮动态注入（plan-only 提醒、一次性注入）。
        var injection = _promptInjector?.BuildInjection(round);
        if (!string.IsNullOrEmpty(injection))
        {
            result.Add(ChatMessage.FromUser(injection));
        }

        // 层1：发送前截断过大的工具结果。
        return _compressor is not null ? _compressor.Truncate(result) : result;
    }

    private (List<ToolCall> Reads, List<ToolCall> Writes) PartitionByCategory(List<ToolCall> toolCalls)
    {
        var reads = new List<ToolCall>();
        var writes = new List<ToolCall>();
        foreach (var call in toolCalls)
        {
            var tool = _toolRegistry.Get(call.Name);
            if (tool is not null && tool.Category == ToolCategory.Read)
            {
                reads.Add(call);
            }
            else
            {
                writes.Add(call);
            }
        }
        return (reads, writes);
    }

    private async Task<IReadOnlyList<ToolResult>> ExecuteConcurrentAsync(
        List<ToolCall> calls, CancellationToken cancellationToken)
    {
        var tasks = calls.Select(c => ExecuteSingleAsync(c, cancellationToken)).ToArray();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<ToolResult> ExecuteSingleAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var tool = _toolRegistry.Get(call.Name);
        if (tool is null)
        {
            return ToolResult.Fail($"未知工具: {call.Name}");
        }
        return await _toolExecutor.ExecuteAsync(tool, call.Arguments, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static void AppendToolResult(ConversationHistory history, ToolCall call, ToolResult result) =>
        history.AddRawMessage(ChatMessage.FromToolResult(call.Id, call.Name, result.ToMessage()));

    private const string DefaultSystemPrompt =
        """
        你是 VestiCode，一个运行在终端中的 AI 编程助手。
        你能通过工具读写文件、执行命令来完成用户的编程任务。

        工作方式（ReAct）：
        - 思考当前状态，决定下一步该用哪个工具；
        - 调用工具并观察结果；
        - 重复，直到任务完成，再用自然语言give出最终答复。

        原则：
        - 修改文件前先 read_file 确认内容；
        - 用 edit_file 做精确替换（old_string 须在文件中唯一）；
        - 不要臆测文件内容，凭工具返回的事实行动；
        - 任务完成时，不再调用工具，直接给出简洁的总结。
        """;
}
