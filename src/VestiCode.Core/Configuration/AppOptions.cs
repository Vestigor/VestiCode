using System.ComponentModel.DataAnnotations;

namespace VestiCode.Core.Configuration;

/// <summary>
/// 应用根配置，绑定自配置节 <c>VestiCode</c>。
/// 通过 <c>IOptions&lt;AppOptions&gt;</c> 注入消费，启动时校验。
/// </summary>
public sealed class AppOptions
{
    /// <summary>配置节名称。</summary>
    public const string SectionName = "VestiCode";

    /// <summary>当前启用的 Provider 名称，须与 <see cref="Providers"/> 中某项的 Name 一致。</summary>
    [Required(AllowEmptyStrings = false)]
    public string ActiveProvider { get; set; } = "";

    /// <summary>可用的 LLM Provider 列表（多后端，运行时按 <see cref="ActiveProvider"/> 选择）。</summary>
    public List<ProviderOptions> Providers { get; set; } = [];

    /// <summary>Agent 循环相关设置。</summary>
    public AgentOptions Agent { get; set; } = new();
}

/// <summary>
/// 单个 LLM Provider 的最小配置：<b>只识别 Name / Model / ApiKey</b>。
/// <see cref="Name"/> 必须是 <c>openai</c> | <c>anthropic</c> | <c>deepseek</c>，
/// 协议与 API 基址由它推导（见 <c>ProviderCatalog</c>）。
/// </summary>
public sealed class ProviderOptions
{
    /// <summary>Provider 名称，须为 openai/anthropic/deepseek 之一；也用于 <see cref="AppOptions.ActiveProvider"/> 引用。</summary>
    [Required(AllowEmptyStrings = false)]
    public string Name { get; set; } = "";

    /// <summary>模型 ID，例如 <c>gpt-4o</c> / <c>claude-sonnet-4-6</c> / <c>deepseek-chat</c>。</summary>
    [Required(AllowEmptyStrings = false)]
    public string Model { get; set; } = "";

    /// <summary>API Key。<b>不写进随程序模板</b>，由全局/项目配置或 UserSecrets / 环境变量提供。</summary>
    public string ApiKey { get; set; } = "";

    // 以下两项不从配置读取，由工厂按 Name 推导后回填，供 Provider 实现使用。
    public string Protocol { get; set; } = "";
    public string BaseUrl { get; set; } = "";
}

/// <summary>Agent 循环设置。</summary>
public sealed class AgentOptions
{
    /// <summary>ReAct 循环单次任务的最大轮次，防止无限循环（有上下文压缩兜底，可放大）。</summary>
    [Range(1, 500)]
    public int MaxRounds { get; set; } = 50;
}
