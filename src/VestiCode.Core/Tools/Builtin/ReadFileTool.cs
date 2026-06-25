using System.Text;
using System.Text.Json.Nodes;

namespace VestiCode.Core.Tools.Builtin;

/// <summary>
/// 读取文件内容（只读工具）。带行号（便于模型引用/编辑），支持 offset/limit 分段读取大文件。
/// </summary>
public sealed class ReadFileTool : ITool
{
    private const int DefaultLimit = 2000;

    // 依次尝试的编码（GBK 等需要 CodePagesEncodingProvider，已在静态构造注册）。
    private static readonly string[] EncodingNames = ["utf-8", "gbk", "latin1"];

    static ReadFileTool() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private readonly string? _root;
    public ReadFileTool() { }
    public ReadFileTool(string root) => _root = root; // 团队成员：根植于其 worktree
    private string Root => _root ?? Directory.GetCurrentDirectory();

    public string Name => "read_file";

    public string Description =>
        "读取文件内容，输出带行号（便于后续 edit_file 精确定位）。可用 offset/limit 分段读取大文件。";

    public ToolCategory Category => ToolCategory.Read;

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new ToolParameter("path", "string", "文件路径，相对于工作目录。"),
        new ToolParameter("offset", "integer", "起始行号（从 1 开始），默认 1。", Required: false),
        new ToolParameter("limit", "integer", $"最多读取的行数，默认 {DefaultLimit}。", Required: false),
    ];

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, CancellationToken cancellationToken = default)
    {
        var path = arguments.GetString("path");
        var offset = Math.Max(1, arguments.GetIntOrDefault("offset", 1));
        var limit = arguments.GetIntOrDefault("limit", DefaultLimit);
        if (limit <= 0)
        {
            limit = DefaultLimit;
        }

        string resolved;
        try
        {
            resolved = WorkspacePath.Resolve(path, Root);
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Fail(ex.Message);
        }

        if (!File.Exists(resolved))
        {
            return ToolResult.Fail($"文件不存在: {path}");
        }

        var bytes = await File.ReadAllBytesAsync(resolved, cancellationToken).ConfigureAwait(false);
        var text = Decode(bytes);
        if (text is null)
        {
            return ToolResult.Fail($"无法解码文件（尝试了 {string.Join(", ", EncodingNames)}）: {path}");
        }

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var total = lines.Length;
        if (offset > total)
        {
            return ToolResult.Ok($"(文件共 {total} 行，offset={offset} 已超出)");
        }

        var end = Math.Min(total, offset - 1 + limit);
        var sb = new StringBuilder();
        for (var i = offset - 1; i < end; i++)
        {
            sb.Append($"{i + 1,6}\t{lines[i]}\n");
        }
        if (end < total)
        {
            sb.Append($"… (还有 {total - end} 行，用 offset={end + 1} 继续读取)");
        }

        return ToolResult.Ok(sb.ToString().TrimEnd('\n'));
    }

    private static string? Decode(byte[] bytes)
    {
        foreach (var encName in EncodingNames)
        {
            try
            {
                var enc = Encoding.GetEncoding(encName, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                var text = enc.GetString(bytes);
                // 去掉前导 BOM（U+FEFF），与 edit_file（File.ReadAllText 已去 BOM）保持一致，
                // 否则 dotnet new 等带 BOM 的文件会让 edit_file 的 old_string 匹配不上。
                return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
            }
            catch (DecoderFallbackException)
            {
                // 换下一种编码
            }
            catch (ArgumentException)
            {
                // 平台无此编码
            }
        }
        return null;
    }
}
