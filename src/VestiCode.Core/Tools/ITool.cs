using System.Text.Json.Nodes;

namespace VestiCode.Core.Tools;

/// <summary>
/// Agent 可调用的工具接口。每个工具把一个 C# 能力描述为 LLM 可理解的函数，
/// 是连接模型“思考”与“行动”的桥梁。
/// </summary>
public interface ITool
{
    /// <summary>工具唯一标识，例如 <c>read_file</c>。</summary>
    string Name { get; }

    /// <summary>给模型看的工具说明。</summary>
    string Description { get; }

    /// <summary>分类：默认写入（保守默认）；只读工具应覆盖为 <see cref="ToolCategory.Read"/>。</summary>
    ToolCategory Category => ToolCategory.Write;

    /// <summary>参数 Schema。</summary>
    IReadOnlyList<ToolParameter> Parameters { get; }

    /// <summary>用给定的命名参数执行工具。</summary>
    /// <param name="arguments">模型提供的参数对象（来自工具调用的 JSON）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<ToolResult> ExecuteAsync(JsonObject arguments, CancellationToken cancellationToken = default);

    /// <summary>导出协议无关的工具定义。</summary>
    ToolDefinition GetDefinition() => new(Name, Description, Parameters);
}
