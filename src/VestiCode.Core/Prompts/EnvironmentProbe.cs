using System.Runtime.InteropServices;

namespace VestiCode.Core.Prompts;

/// <summary>收集环境信息，作为系统提示的一部分注入。</summary>
public static class EnvironmentProbe
{
    public static string Collect()
    {
        var cwd = Directory.GetCurrentDirectory();
        var os = RuntimeInformation.OSDescription;
        var now = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'");
        return $"工作目录: {cwd}\n操作系统: {os}\n当前时间: {now}";
    }
}
