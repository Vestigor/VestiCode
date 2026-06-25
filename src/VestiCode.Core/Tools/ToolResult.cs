namespace VestiCode.Core.Tools;

/// <summary>
/// 工具执行的结构化结果。失败时也返回结构化信息（而非抛异常），
/// 以便把失败原因回灌给模型，让 Agent 据此调整。
/// </summary>
/// <param name="Success">是否成功。</param>
/// <param name="Content">成功时的输出内容；失败时可附带部分输出。</param>
/// <param name="Error">失败原因（成功时为空）。</param>
public sealed record ToolResult(bool Success, string Content, string Error = "")
{
    /// <summary>构造成功结果。</summary>
    public static ToolResult Ok(string content) => new(true, content);

    /// <summary>构造失败结果。</summary>
    public static ToolResult Fail(string error, string content = "") => new(false, content, error);

    /// <summary>渲染为回灌进对话的纯文本（失败时带上原因与输出）。</summary>
    public string ToMessage() =>
        Success ? Content : $"工具执行失败: {Error}\n\n输出:\n{Content}";
}
