using VestiCode.Core.Configuration;

namespace VestiCode.Core.Llm;

/// <summary>按 <see cref="ProviderOptions.Protocol"/> 创建对应的 <see cref="ILlmProvider"/> 实现。</summary>
public interface ILlmProviderFactory
{
    /// <summary>为给定配置创建 Provider；协议不支持时抛 <see cref="NotSupportedException"/>。</summary>
    ILlmProvider Create(ProviderOptions config);
}
