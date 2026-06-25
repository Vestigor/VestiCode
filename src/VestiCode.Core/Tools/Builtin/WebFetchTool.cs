using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VestiCode.Core.Tools.Builtin;

/// <summary>抓取一个 URL 并返回可读文本（去除 HTML 标签/脚本），用于查阅文档/网页（只读工具）。</summary>
public sealed partial class WebFetchTool(IHttpClientFactory httpClientFactory) : ITool
{
    private const int OutputLimit = 10_000;

    [GeneratedRegex(@"<script\b[^>]*>.*?</script>|<style\b[^>]*>.*?</style>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptStyleRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLinesRegex();

    public string Name => "web_fetch";

    public string Description => "抓取一个 http(s) URL 的内容并返回纯文本（自动去除 HTML 标签），用于查阅在线文档或网页。";

    public ToolCategory Category => ToolCategory.Read;

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new ToolParameter("url", "string", "要抓取的 http/https URL。"),
    ];

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, CancellationToken cancellationToken = default)
    {
        var url = arguments.GetString("url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return ToolResult.Fail($"无效的 URL: {url}");
        }

        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "VestiCode/0.1");

        try
        {
            using var resp = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return ToolResult.Fail($"HTTP {(int)resp.StatusCode} {resp.StatusCode}");
            }

            var raw = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var text = ToReadableText(raw);
            var truncated = text.Length > OutputLimit;
            if (truncated)
            {
                text = text[..OutputLimit] + "\n…(内容已截断)";
            }
            return ToolResult.Ok($"来源: {uri}\n\n{text}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"抓取失败: {ex.Message}");
        }
    }

    private static string ToReadableText(string html)
    {
        var noScript = ScriptStyleRegex().Replace(html, " ");
        var noTags = TagRegex().Replace(noScript, " ");
        var decoded = WebUtility.HtmlDecode(noTags);
        // 折叠空白与多余空行。
        var lines = decoded.Split('\n').Select(l => l.Trim());
        var joined = string.Join("\n", lines);
        joined = Regex.Replace(joined, "[ \t]{2,}", " ");
        return BlankLinesRegex().Replace(joined, "\n\n").Trim();
    }
}
