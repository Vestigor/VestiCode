namespace VestiCode.Core.Security;

/// <summary>路径沙箱：校验文件类工具的路径不越出项目目录边界。</summary>
public sealed class PathSandbox
{
    private readonly string _projectRoot;

    public PathSandbox(string? projectRoot = null) =>
        _projectRoot = Path.GetFullPath(projectRoot ?? Directory.GetCurrentDirectory());

    public string ProjectRoot => _projectRoot;

    /// <summary>校验 <paramref name="path"/> 是否安全。返回 <c>(是否安全, 原因)</c>。</summary>
    public (bool Safe, string Message) Validate(string path)
    {
        string resolved;
        try
        {
            resolved = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(_projectRoot, path));
        }
        catch (Exception)
        {
            return (false, $"无效路径: {path}");
        }

        if (!IsWithinRoot(resolved))
        {
            return Path.IsPathRooted(path)
                ? (false, $"路径超出项目目录: {path}（绝对路径不在 {_projectRoot} 内）")
                : (false, $"路径遍历不被允许: {path}（解析后 {resolved} 不在项目目录内）");
        }

        // 额外拦截相对路径中显式出现的 ".." 段。
        if (!Path.IsPathRooted(path) && SplitSegments(path).Contains(".."))
        {
            return (false, $"路径包含 '..' 不被允许: {path}");
        }

        return (true, "");
    }

    /// <summary>路径是否匹配任一允许的 glob。</summary>
    public static bool IsWithinAllowed(string path, IEnumerable<string> allowedGlobs) =>
        allowedGlobs.Any(g => Glob.IsMatch(g, path));

    private bool IsWithinRoot(string resolved)
    {
        var root = _projectRoot.TrimEnd(Path.DirectorySeparatorChar);
        return string.Equals(resolved, root, StringComparison.Ordinal)
               || resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static IEnumerable<string> SplitSegments(string path) =>
        path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
}
