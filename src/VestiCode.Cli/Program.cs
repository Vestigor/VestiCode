using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using VestiCode.Cli;
using VestiCode.Cli.Tui;
using VestiCode.Core.Configuration;
using VestiCode.Core.DependencyInjection;

// Windows 控制台默认用本地码页（中文系统=GBK/936），无法渲染 TUI 用到的
// Unicode 字形（❯ ● │ ✻ 等）→ 在 cmd 里全显示成“？”。统一切到 UTF-8。
// 须在任何控制台输出之前执行。输出被重定向等场景下设置可能抛异常，忽略即可。
if (OperatingSystem.IsWindows())
{
    try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* 已重定向，忽略 */ }
    try { Console.InputEncoding = System.Text.Encoding.UTF8; } catch { /* 已重定向，忽略 */ }
}

// 自举 logger：覆盖 Host 构建期间（DI 容器尚未就绪）的早期日志，
// 之后由下方 AddSerilog 注册的正式 logger 接管。
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    // 内容根设为可执行文件所在目录，使随程序分发的 appsettings.json 默认配置
    // 不受调用方当前工作目录影响（CLI 可从任意目录启动）。
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
    });

    // 启动即建好全局目录骨架（~/.vesticode 及其子目录），便于用户放置配置/查看记忆。
    var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var vcRoot = Path.Combine(homeDir, ".vesticode");
    foreach (var sub in new[] { "", "skills", "roles", "notes", "sessions", "tool_results", "teams", "logs" })
    {
        Directory.CreateDirectory(Path.Combine(vcRoot, sub));
    }
    var logPath = Path.Combine(vcRoot, "logs", "vesticode-.log");

    // 配置分层（后者覆盖前者）：用户全局 ~/.vesticode/appsettings.json → 项目 ./.vesticode/appsettings.json
    // → VESTICODE_CONFIG 指定文件 → 环境变量 → UserSecrets。仅承载 LLM 配置（Name/Model/ApiKey）。
    // 不读随程序模板：模板仅作参考（config-examples/example.appsettings.json），不参与配置。
    var userConfigPath = Path.Combine(vcRoot, "appsettings.json");
    builder.Configuration.AddJsonFile(userConfigPath, optional: true, reloadOnChange: false);
    builder.Configuration.AddJsonFile(
        Path.Combine(Directory.GetCurrentDirectory(), ".vesticode", "appsettings.json"), optional: true, reloadOnChange: false);
    // VESTICODE_CONFIG：显式指定配置文件路径，优先级高于项目/全局（对应验收的 MEWCODE_CONFIG）。
    var configEnv = Environment.GetEnvironmentVariable("VESTICODE_CONFIG");
    if (!string.IsNullOrEmpty(configEnv))
    {
        builder.Configuration.AddJsonFile(configEnv, optional: true, reloadOnChange: false);
    }
    builder.Configuration.AddUserSecrets<Program>(optional: true);

    // 友好环境变量 VESTICODE_API_KEY → 注入当前激活 Provider 的 Key（仅在其未配置时）。
    var snapshot = builder.Configuration.GetSection(AppOptions.SectionName).Get<AppOptions>() ?? new AppOptions();
    var activeIdx = snapshot.Providers.FindIndex(p => p.Name == snapshot.ActiveProvider);
    var friendlyKey = Environment.GetEnvironmentVariable("VESTICODE_API_KEY");
    if (activeIdx >= 0 && !string.IsNullOrEmpty(friendlyKey) && string.IsNullOrEmpty(snapshot.Providers[activeIdx].ApiKey))
    {
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{AppOptions.SectionName}:Providers:{activeIdx}:ApiKey"] = friendlyKey,
        });
        snapshot.Providers[activeIdx].ApiKey = friendlyKey;
    }

    // 首次运行：交互式（非管道）且当前 Provider 缺 Key → 引导用户完成配置，写入用户配置后重新加载。
    var activeProvider = activeIdx >= 0 ? snapshot.Providers[activeIdx] : null;
    if (!Console.IsInputRedirected && SetupWizard.NeedsSetup(snapshot.ActiveProvider, activeProvider?.ApiKey))
    {
        SetupWizard.Run(userConfigPath);
        builder.Configuration.AddJsonFile(userConfigPath, optional: false, reloadOnChange: false); // 重新加载新配置
    }

    // Serilog 接管 Microsoft.Extensions.Logging：日志只写文件，不挂控制台 sink
    // （内联 UI 自己渲染错误；控制台日志会和活动区域/光标刷新打架，导致显示错乱）。
    builder.Services.AddSerilog((_, lc) =>
    {
        lc.Enrich.FromLogContext()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day);
    });

    // 强类型配置绑定 + 启动期校验（DataAnnotations + 自定义规则），
    // 配置非法时进程启动即失败，而非运行中才暴露。
    builder.Services
        .AddOptions<AppOptions>()
        .Bind(builder.Configuration.GetSection(AppOptions.SectionName))
        .ValidateDataAnnotations()
        .Validate(
            o => o.Providers.Exists(p => p.Name == o.ActiveProvider),
            "ActiveProvider 未在 Providers 列表中找到对应项")
        .ValidateOnStart();

    // 注册领域核心（Provider / 工具 / 执行器 / Agent 循环）与内联终端 UI。
    builder.Services.AddVestiCodeAgent();
    builder.Services.AddSingleton<ConsoleApp>();

    using var host = builder.Build();

    var logger = host.Services.GetRequiredService<ILogger<Program>>();
    var options = host.Services.GetRequiredService<IOptions<AppOptions>>().Value;
    var active = options.Providers.Find(p => p.Name == options.ActiveProvider)!;

    VestiCode.Core.Llm.ProviderCatalog.TryResolve(active.Name, out var activeProtocol, out _);
    logger.LogInformation(
        "VestiCode 启动 — 活动 Provider: {Provider} (protocol={Protocol}, model={Model}, maxRounds={MaxRounds})",
        active.Name, activeProtocol, active.Model, options.Agent.MaxRounds);

    if (string.IsNullOrEmpty(active.ApiKey))
    {
        logger.LogWarning(
            "活动 Provider '{Provider}' 未配置 API Key；请用 UserSecrets 或环境变量设置后再运行实际任务。",
            active.Name);
    }

    var promptBuilder = host.Services.GetRequiredService<VestiCode.Core.Prompts.PromptBuilder>();

    // 项目/用户指令（项目根 VESTICODE.md + 全局 ~/.vesticode/instructions.md，支持 @include）。
    var instructions = new VestiCode.Core.Instructions.InstructionsLoader().Load();
    if (!string.IsNullOrWhiteSpace(instructions))
    {
        promptBuilder.AddSection($"## 项目指令\n{instructions}");
    }

    // Skill 阶段1：扫描内置+全局+项目目录，把可用 Skill 的名字+描述注入系统提示。
    var skillRegistry = host.Services.GetRequiredService<VestiCode.Core.Skills.SkillRegistry>();
    var available = skillRegistry.LoadAll();
    if (available.Count > 0)
    {
        var list = string.Join("\n", available.Select(s => $"- {s.Name}: {s.Description}"));
        promptBuilder.AddSection($"## 可用 Skill\n用 skill_loader 工具按需激活：\n{list}");
    }

    // MCP：加载配置并连接外部 server，把其工具注册进工具表。
    // 连接超时由各 server 配置的 timeout 参数（默认 30s）控制。
    var mcp = host.Services.GetRequiredService<VestiCode.Core.Mcp.McpManager>();
    if (mcp.LoadConfig().Count > 0)
    {
        var registered = await mcp.DiscoverAndRegisterAsync();
        logger.LogInformation("MCP：已连接 {Servers}，注册 {Count} 个适配器",
            string.Join(", ", mcp.ConnectedServers), registered);
    }

    // Skill 白名单校验：声明了未知工具的 Skill 记录告警（工具表此时已含 MCP 适配器）。
    var toolNames = host.Services.GetRequiredService<VestiCode.Core.Tools.ToolRegistry>()
        .Tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
    foreach (var warning in skillRegistry.ValidateWhitelists(toolNames))
    {
        logger.LogWarning("{Warning}", warning);
    }

    // --resume：启动时把最近一次会话载入共享历史。
    if (args.Contains("--resume"))
    {
        var loaded = host.Services.GetRequiredService<VestiCode.Core.Memory.JsonlSessionStore>().Load(null);
        if (loaded is not null)
        {
            host.Services.GetRequiredService<VestiCode.Core.Conversation.ConversationHistory>()
                .ReplaceMessages(loaded.Value.History.GetMessages());
            logger.LogInformation("已恢复上次会话（{Count} 条消息）", loaded.Value.History.Count);
        }
    }

    // --dispatch：打开调度模式的第二把锁（配置/CLI）。第一把锁由 TUI 内 /dispatch 切换。
    if (args.Contains("--dispatch"))
    {
        host.Services.GetRequiredService<VestiCode.Core.Teams.DispatchScheduler>().SetLock2(true);
        logger.LogInformation("调度模式第二把锁已开启（--dispatch）；在会话内用 /dispatch 开启第一把锁以激活。");
    }

    // 后台清理过期 worktree。
    var cleaner = host.Services.GetRequiredService<VestiCode.Core.Worktree.BackgroundCleaner>();
    cleaner.Start();

    // 子进程清理（MCP server / 持久 shell）。Ctrl+C(SIGINT) 不会跑 finally，
    // 故同时挂到 ProcessExit，保证任何退出路径都杀掉子进程，避免 node/sh 泄漏累积导致后续启动被系统 kill。
    var shell = host.Services.GetRequiredService<VestiCode.Core.Tools.Builtin.PersistentShell>();
    var cleanedUp = false;
    void CleanupChildren()
    {
        if (cleanedUp)
        {
            return;
        }
        cleanedUp = true;
        try { mcp.ShutdownAsync().GetAwaiter().GetResult(); } catch { /* 尽力清理 */ }
        try { shell.Dispose(); } catch { /* 尽力清理 */ }
    }
    AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupChildren();

    try
    {
        await host.Services.GetRequiredService<ConsoleApp>().RunAsync();
    }
    finally
    {
        await cleaner.StopAsync();
        CleanupChildren();
    }
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "VestiCode 启动失败");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>程序入口标记类型，供 <c>AddUserSecrets&lt;Program&gt;</c> 定位程序集。</summary>
public partial class Program;
