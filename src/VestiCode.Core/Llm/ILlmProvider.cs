using VestiCode.Core.Configuration;
using VestiCode.Core.Tools;

namespace VestiCode.Core.Llm;

/// <summary>
/// LLM 后端抽象。各实现负责：把协议无关的消息/工具定义序列化为自家线缆格式，
/// 发起 SSE 流式请求，并把响应解析回统一的 <see cref="LlmStreamItem"/> 流。
/// </summary>
public interface ILlmProvider
{
    /// <summary>该 Provider 的连接配置。</summary>
    ProviderOptions Config { get; }

    /// <summary>是否支持 extended thinking / reasoning。</summary>
    bool SupportsThinking => false;

    /// <summary>
    /// 发送消息并以 SSE 流式返回 token / 思考 / 工具调用 / 用量 / 错误。
    /// </summary>
    /// <param name="messages">有序消息列表（可含一条 System 消息，由实现按协议处理）。</param>
    /// <param name="tools">可选的工具定义（协议无关，由实现转换为原生格式）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    IAsyncEnumerable<LlmStreamItem> ChatStreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools = null,
        CancellationToken cancellationToken = default);
}
