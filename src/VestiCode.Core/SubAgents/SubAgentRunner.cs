using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VestiCode.Core.Agents;
using VestiCode.Core.Configuration;
using VestiCode.Core.Conversation;
using VestiCode.Core.Llm;
using VestiCode.Core.Prompts;
using VestiCode.Core.Tools;

namespace VestiCode.Core.SubAgents;

/// <summary>
/// 子 Agent 运行器：在一次调用内把子 Agent 跑到完成（run-to-end）。
/// 子 Agent 用过滤后的工具集与独立历史；不带安全 HITL（由 ToolFilter 约束），不递归。
/// </summary>
public sealed class SubAgentRunner(
    ILlmProvider provider,
    IServiceProvider serviceProvider,
    ToolExecutor toolExecutor,
    ILoggerFactory loggerFactory,
    RoleLoader roleLoader)
{
    private const string ForkInstruction =
        """
        [Fork 模式] 你是一个子工作器。遵守以下规则：
        - 不要再创建子工作器（sub_agent 不可用）
        - 不要主动对话、不要请求确认、不要问用户问题
        - 直接使用工具完成任务，不需要征求许可
        - 完成后输出结构化报告，控制在 500 字以内
        - 报告格式：## 结果摘要 / ## 关键发现 / ## 文件与代码 / ## 建议
        """;

    private readonly IReadOnlyDictionary<string, SubAgentRole> _roles = roleLoader.LoadAll();

    public IReadOnlyDictionary<string, SubAgentRole> Roles => _roles;

    /// <summary>把子任务跑到完成，返回最终报告文本。</summary>
    public async Task<string> RunAsync(SubAgentTask task, ConversationHistory parentHistory, CancellationToken cancellationToken = default)
    {
        var role = task.Role is not null ? _roles.GetValueOrDefault(task.Role) : null;
        var isFork = role is null;

        // 延迟解析主工具注册表（避免 DI 构造期循环：ToolRegistry→SubAgentTool→SubAgentRunner）。
        var toolRegistry = serviceProvider.GetRequiredService<ToolRegistry>();

        // 工具过滤 → 构造仅含允许工具的子注册表。
        var allTools = toolRegistry.Tools.Select(t => t.Name).ToList();
        var allowed = ToolFilter.Filter(allTools, role).ToHashSet(StringComparer.Ordinal);
        var filteredRegistry = new ToolRegistry();
        filteredRegistry.RegisterRange(toolRegistry.Tools.Where(t => allowed.Contains(t.Name)));

        // 子历史。
        var subHistory = new ConversationHistory();
        if (isFork)
        {
            subHistory.ReplaceMessages(parentHistory.GetMessages());
            subHistory.AddUserMessage($"{ForkInstruction}\n\n任务: {task.Task}");
        }
        else
        {
            subHistory.AddUserMessage($"{role!.SystemPrompt}\n\n任务: {task.Task}");
        }

        var options = Options.Create(new AppOptions { Agent = new AgentOptions { MaxRounds = role?.MaxRounds ?? 3 } });
        var subLoop = new AgentLoop(
            provider,
            filteredRegistry,
            toolExecutor,
            options,
            loggerFactory.CreateLogger<AgentLoop>(),
            promptBuilder: new PromptBuilder());

        // 联动父级取消与本任务的 /tasks kill 取消。
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, task.CancelToken);

        var finalText = new System.Text.StringBuilder();
        var rounds = 0;
        await foreach (var ev in subLoop.RunAsync(subHistory, linked.Token).ConfigureAwait(false))
        {
            switch (ev)
            {
                case RoundStartEvent:
                    rounds++;
                    break;
                case TextDeltaEvent td:
                    finalText.Append(td.Text);
                    break;
                case AgentDoneEvent done:
                    // 在轮次边界被 kill：AgentLoop 产出 Cancelled。
                    if (done.Reason == AgentDoneReason.Cancelled && task.CancelRequested)
                    {
                        task.MarkCancelled();
                        return "[子 Agent 已被终止]";
                    }
                    task.Complete(finalText.ToString(), rounds);
                    return finalText.ToString();
            }
        }

        task.Complete(finalText.ToString(), rounds);
        return finalText.ToString();
    }
}
