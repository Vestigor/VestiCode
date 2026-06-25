using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VestiCode.Core.Agents;
using VestiCode.Core.Commands;
using VestiCode.Core.Commands.Builtin;
using VestiCode.Core.Configuration;
using VestiCode.Core.Conversation;
using VestiCode.Core.Hooks;
using VestiCode.Core.Llm;
using VestiCode.Core.Mcp;
using VestiCode.Core.Memory;
using VestiCode.Core.Notes;
using VestiCode.Core.Prompts;
using VestiCode.Core.Security;
using VestiCode.Core.Skills;
using VestiCode.Core.SubAgents;
using VestiCode.Core.Teams;
using VestiCode.Core.Tools;
using VestiCode.Core.Tools.Builtin;
using VestiCode.Core.Worktree;

namespace VestiCode.Core.DependencyInjection;

/// <summary>把 VestiCode 领域核心的全部服务登记进 DI 容器。</summary>
public static class CoreServiceCollectionExtensions
{
    /// <summary>注册 Provider、工具、执行器与 Agent 循环。</summary>
    public static IServiceCollection AddVestiCodeAgent(this IServiceCollection services)
    {
        // 供 LLM 调用的命名 HttpClient：流式响应可能很长，超时交给 CancellationToken 控制；
        // 挂上重试处理器做瞬时失败（429/5xx/网络）指数退避（生产硬化）。
        services.AddHttpClient(LlmProviderFactory.HttpClientName, c => c.Timeout = Timeout.InfiniteTimeSpan)
            .AddHttpMessageHandler(sp => new RetryHandler(
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<RetryHandler>()));

        // 持久 shell 会话（cd/env 跨调用保持）。
        services.AddSingleton<PersistentShell>();

        // 内置工具（每个 ITool 一个登记，再由 ToolRegistry 汇总）。
        services.AddSingleton<ITool, ReadFileTool>();
        services.AddSingleton<ITool, WriteFileTool>();
        services.AddSingleton<ITool, EditFileTool>();
        services.AddSingleton<ITool, RunCommandTool>();
        services.AddSingleton<ITool, GlobTool>();
        services.AddSingleton<ITool, GrepTool>();
        services.AddSingleton<ITool, WebFetchTool>();
        services.AddSingleton(sp =>
        {
            var registry = new ToolRegistry();
            registry.RegisterRange(sp.GetServices<ITool>());
            return registry;
        });
        services.AddSingleton<ToolExecutor>();

        // 安全：沙箱 + 策略 + 守卫（构造参数均有默认值，DI 用默认 = Normal 档 + 当前工作目录）。
        services.AddSingleton<PathSandbox>();
        services.AddSingleton<SecurityPolicy>();
        services.AddSingleton<SecurityGuard>();

        // Skill 系统（三级加载 + 两阶段激活）；skill_loader 作为系统工具登记。
        services.AddSingleton<SkillLoader>();
        services.AddSingleton<SkillRegistry>();
        services.AddSingleton<ITool, SkillTool>();

        // 子 Agent（多 Agent 协作）：共享对话历史供 fork 模式继承。
        services.AddSingleton<ConversationHistory>();
        services.AddSingleton<RoleLoader>();
        services.AddSingleton<SubAgentManager>();
        services.AddSingleton<SubAgentRunner>();
        services.AddSingleton<ITool, SubAgentTool>();

        // Git worktree 隔离 + 后台清理。
        services.AddSingleton<GitWorktreeManager>();
        services.AddSingleton<BackgroundCleaner>();

        // Agent Team 编排（多 Agent）。
        services.AddSingleton<DispatchScheduler>();
        services.AddSingleton<TeamManager>();

        // MCP 客户端管理器（启动时连接外部 MCP server 并注册其工具）。
        services.AddSingleton<McpManager>();

        // Hook 引擎（启动时从 YAML 加载规则）。
        services.AddSingleton<HookLoader>();
        services.AddSingleton(sp => new ActionExecutor(
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<ActionExecutor>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient()));
        services.AddSingleton(sp => new HookEngine(
            sp.GetRequiredService<HookLoader>().Load(),
            sp.GetRequiredService<ActionExecutor>()));

        // Provider：工厂按当前激活配置创建。
        services.AddSingleton<ILlmProviderFactory, LlmProviderFactory>();
        services.AddSingleton<ILlmProvider>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<AppOptions>>().Value;
            var active = opts.Providers.First(p => p.Name == opts.ActiveProvider);
            return sp.GetRequiredService<ILlmProviderFactory>().Create(active);
        });

        // 会话持久化（长期记忆）。
        services.AddSingleton<JsonlSessionStore>();

        // 自动笔记（每 5 轮 LLM 增量更新四分类）。
        services.AddSingleton(sp => new AutoNoteManager(
            sp.GetRequiredService<ILlmProvider>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<AutoNoteManager>()));

        // 两层上下文压缩（截断 + 结构化摘要），用当前 Provider 与模型构造。
        services.AddSingleton(sp =>
        {
            var provider = sp.GetRequiredService<ILlmProvider>();
            return new ContextCompressor(provider.Config.Model, provider);
        });

        // 模块化 Prompt。
        services.AddSingleton<PromptBuilder>();
        services.AddSingleton<PromptInjector>();

        // 斜杠命令（每个 ICommand 一个登记，再由 CommandRegistry 汇总）。
        services.AddSingleton<ICommand, HelpCommand>();
        services.AddSingleton<ICommand, ClearCommand>();
        services.AddSingleton<ICommand, StatusCommand>();
        services.AddSingleton<ICommand, ModeCommand>();
        services.AddSingleton<ICommand, PlanCommand>();
        services.AddSingleton<ICommand, DispatchCommand>();
        services.AddSingleton<ICommand, PermissionCommand>();
        services.AddSingleton<ICommand, CompressCommand>();
        services.AddSingleton<ICommand, ReviewCommand>();
        services.AddSingleton<ICommand, SessionCommand>();
        services.AddSingleton<ICommand, SkillCommand>();
        services.AddSingleton<ICommand, MemoryCommand>();
        services.AddSingleton<ICommand, CommitCommand>();
        services.AddSingleton<ICommand, TestCommand>();
        services.AddSingleton<ICommand, TasksCommand>();
        services.AddSingleton<ICommand, WorktreeCommand>();
        services.AddSingleton<ICommand, TeamCommand>();
        services.AddSingleton(sp =>
        {
            var registry = new CommandRegistry();
            registry.RegisterRange(sp.GetServices<ICommand>());
            return registry;
        });

        services.AddSingleton<AgentLoop>();

        return services;
    }
}
