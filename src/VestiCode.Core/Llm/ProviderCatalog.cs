using System.Text.Json;

namespace VestiCode.Core.Llm;

/// <summary>
/// Provider 推导表：从嵌入资源 <c>providers.json</c>（开发者维护）加载 Name → (Protocol, BaseUrl) 映射。
/// 用户最小配置只需 <c>Name</c>/<c>Model</c>/<c>ApiKey</c>，协议与基址由此推导。
/// 加新后端只需改 JSON，无需改代码。
/// </summary>
public static class ProviderCatalog
{
    private const string Resource = "VestiCode.Core.Llm.providers.json";

    private sealed record ProviderProfile(string Name, string Protocol, string BaseUrl);

    private static readonly IReadOnlyDictionary<string, (string Protocol, string BaseUrl)> Map = Load();

    /// <summary>受支持的 Provider 名称（即合法的 <c>Name</c> 取值）。</summary>
    public static IReadOnlyCollection<string> Names => (IReadOnlyCollection<string>)Map.Keys;

    public static bool IsKnown(string? name) => name is not null && Map.ContainsKey(name);

    /// <summary>由 Name 解析协议与基址；未知 Name 返回 false。</summary>
    public static bool TryResolve(string? name, out string protocol, out string baseUrl)
    {
        if (name is not null && Map.TryGetValue(name, out var v))
        {
            (protocol, baseUrl) = v;
            return true;
        }
        protocol = "";
        baseUrl = "";
        return false;
    }

    private static IReadOnlyDictionary<string, (string, string)> Load()
    {
        var assembly = typeof(ProviderCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException($"内置 Provider 推导表缺失: {Resource}");
        using var reader = new StreamReader(stream);

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip, // 允许 JSON 内注释，便于开发标注
            AllowTrailingCommas = true,
        };
        var profiles = JsonSerializer.Deserialize<List<ProviderProfile>>(reader.ReadToEnd(), options) ?? [];

        var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in profiles)
        {
            if (!string.IsNullOrWhiteSpace(p.Name))
            {
                map[p.Name] = (p.Protocol, p.BaseUrl);
            }
        }
        return map;
    }
}
