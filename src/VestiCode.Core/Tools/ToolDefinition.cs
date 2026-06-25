namespace VestiCode.Core.Tools;

/// <summary>单个工具参数的描述（用于生成 LLM 可理解的 JSON Schema）。</summary>
/// <param name="Name">参数名。</param>
/// <param name="Type">JSON Schema 类型：<c>string</c> / <c>integer</c> / <c>boolean</c> 等。</param>
/// <param name="Description">给模型看的参数说明。</param>
/// <param name="Required">是否必填。</param>
public sealed record ToolParameter(string Name, string Type, string Description, bool Required = true);

/// <summary>
/// 工具的协议无关定义（名称 + 描述 + 参数）。
/// 由各 Provider 转换为其原生格式（OpenAI function / Anthropic tool）。
/// </summary>
/// <param name="Name">工具唯一标识。</param>
/// <param name="Description">给模型看的工具说明。</param>
/// <param name="Parameters">参数列表。</param>
public sealed record ToolDefinition(
    string Name,
    string Description,
    IReadOnlyList<ToolParameter> Parameters);
