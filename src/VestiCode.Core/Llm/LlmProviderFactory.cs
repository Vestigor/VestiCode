using System.Net.Http;
using Microsoft.Extensions.Logging;
using VestiCode.Core.Configuration;

namespace VestiCode.Core.Llm;

/// <summary>
/// 默认工厂：用 <see cref="IHttpClientFactory"/> 取得 HttpClient，按协议族构造 Provider。
/// </summary>
public sealed class LlmProviderFactory(
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory) : ILlmProviderFactory
{
    /// <summary>命名 HttpClient 的逻辑名（在组合根用 AddHttpClient 配置）。</summary>
    public const string HttpClientName = "llm";

    public ILlmProvider Create(ProviderOptions config)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);

        // 由 Name 推导协议与基址（最小配置：只认 Name/Model/ApiKey）。
        if (!ProviderCatalog.TryResolve(config.Name, out var protocol, out var baseUrl))
        {
            throw new NotSupportedException(
                $"不支持的 Provider: {config.Name}（支持 {string.Join("/", ProviderCatalog.Names)}）");
        }
        config.Protocol = protocol;
        config.BaseUrl = baseUrl;

        return protocol switch
        {
            // DeepSeek 与 OpenAI 同协议，复用同一实现（仅 base_url/model 不同）。
            "openai" or "deepseek" =>
                new OpenAIProvider(config, http, loggerFactory.CreateLogger<OpenAIProvider>()),

            "anthropic" =>
                new AnthropicProvider(config, http, loggerFactory.CreateLogger<AnthropicProvider>()),

            _ => throw new NotSupportedException($"不支持的协议: {protocol}"),
        };
    }
}
