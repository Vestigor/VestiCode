namespace VestiCode.Core.Tools;

/// <summary>
/// 工具注册中心：按名查找工具，并导出协议无关的工具定义列表
/// （由 Provider 进一步转换为各自的原生格式）。
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.Ordinal);

    /// <summary>注册一个工具（同名覆盖）。</summary>
    public void Register(ITool tool) => _tools[tool.Name] = tool;

    /// <summary>批量注册。</summary>
    public void RegisterRange(IEnumerable<ITool> tools)
    {
        foreach (var tool in tools)
        {
            Register(tool);
        }
    }

    /// <summary>按名查找，找不到返回 <c>null</c>。</summary>
    public ITool? Get(string name) => _tools.GetValueOrDefault(name);

    /// <summary>所有已注册工具。</summary>
    public IReadOnlyCollection<ITool> Tools => _tools.Values;

    /// <summary>导出全部工具的协议无关定义。</summary>
    public IReadOnlyList<ToolDefinition> GetDefinitions()
        => _tools.Values.Select(t => t.GetDefinition()).ToList();
}
