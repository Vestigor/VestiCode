using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace VestiCode.Core.Mcp;

/// <summary>单个 MCP server 的配置。</summary>
public sealed class McpServerConfig
{
    public string Name { get; set; } = "";
    public string Transport { get; set; } = ""; // "stdio" | "http"
    public string Command { get; set; } = "";
    public List<string> Args { get; set; } = [];
    public Dictionary<string, string> Env { get; set; } = [];
    public string Url { get; set; } = "";
    public Dictionary<string, string> Headers { get; set; } = [];
    public double Timeout { get; set; } = 30.0;
}

/// <summary>
/// 从 YAML 加载 MCP server 配置：项目 <c>./.vesticode/mcp.yaml</c> + 全局 <c>~/.vesticode/mcp.yaml</c>
/// （按 name 取并集，项目覆盖全局同名）。无内置默认：没有配置文件即没有 server。
/// 占位符在加载时展开。
/// </summary>
public static class McpConfigLoader
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static string ProjectPath =>
        Path.Combine(Directory.GetCurrentDirectory(), ".vesticode", "mcp.yaml");

    private static string GlobalPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vesticode", "mcp.yaml");

    public static IReadOnlyList<McpServerConfig> Load()
    {
        var servers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
        foreach (var entry in LoadFile(GlobalPath).Concat(LoadFile(ProjectPath)))
        {
            servers[entry.Name] = Expand(entry); // 项目覆盖全局同名
        }
        return servers.Values.ToList();
    }

    private static List<McpServerConfig> LoadFile(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }
        try
        {
            var file = Yaml.Deserialize<ServersFile>(File.ReadAllText(path));
            return file?.Servers ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// 展开占位符，使一份（尤其全局）配置可跨项目复用：
    /// <c>${cwd}</c>/<c>${workspaceRoot}</c> → 当前工作目录；<c>${home}</c> → 用户主目录；
    /// <c>${env:NAME}</c> → 环境变量。Args 额外做路径解析（见 <see cref="ResolveArg"/>）。
    /// </summary>
    private static McpServerConfig Expand(McpServerConfig c)
    {
        c.Command = ExpandVars(c.Command);
        c.Url = ExpandVars(c.Url);
        c.Args = c.Args.Select(ResolveArg).ToList();
        c.Env = c.Env.ToDictionary(kv => kv.Key, kv => ExpandVars(kv.Value));
        c.Headers = c.Headers.ToDictionary(kv => kv.Key, kv => ExpandVars(kv.Value));
        return c;
    }

    /// <summary>
    /// 参数解析：先展开占位符；若结果是<b>路径类</b>参数（绝对路径或 ./ ../ 开头），
    /// 则「为有效目录时用该目录，否则回退当前工作目录」——保证 fs server 永远拿到可用目录。
    /// 非路径参数（如 <c>-y</c>、<c>@scope/pkg</c>）原样保留。
    /// </summary>
    private static string ResolveArg(string s)
    {
        var v = ExpandVars(s);
        if (IsPathLike(v))
        {
            return Directory.Exists(v) ? v : Directory.GetCurrentDirectory();
        }
        return v;
    }

    private static bool IsPathLike(string s) =>
        Path.IsPathRooted(s) || s == "." || s == ".."
        || s.StartsWith("./", StringComparison.Ordinal) || s.StartsWith("../", StringComparison.Ordinal)
        || s.StartsWith(".\\", StringComparison.Ordinal) || s.StartsWith("..\\", StringComparison.Ordinal);

    private static string ExpandVars(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return s;
        }
        var cwd = Directory.GetCurrentDirectory();
        s = s.Replace("${cwd}", cwd, StringComparison.Ordinal)
             .Replace("${workspaceRoot}", cwd, StringComparison.Ordinal)
             .Replace("${home}", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.Ordinal);
        return Regex.Replace(s, @"\$\{env:([^}]+)\}",
            m => Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? "");
    }

    private sealed class ServersFile
    {
        public List<McpServerConfig>? Servers { get; set; }
    }
}
