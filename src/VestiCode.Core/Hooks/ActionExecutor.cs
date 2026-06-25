using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace VestiCode.Core.Hooks;

/// <summary>动作执行器：shell / prompt_inject / http / sub_agent，错误隔离（只记日志不抛）。</summary>
public sealed class ActionExecutor(ILogger<ActionExecutor> logger, HttpClient httpClient)
{
    /// <summary>执行一个动作，返回结果文本或 null。</summary>
    public async Task<string?> ExecuteAsync(HookAction action, IReadOnlyDictionary<string, object?> context, double timeoutSeconds)
    {
        try
        {
            return action.Type switch
            {
                ActionType.Shell => await ExecShellAsync(action, context, timeoutSeconds).ConfigureAwait(false),
                ActionType.PromptInject => TemplateEngine.Render(action.Text, context),
                ActionType.Http => await ExecHttpAsync(action, context, timeoutSeconds).ConfigureAwait(false),
                ActionType.SubAgent => $"[sub_agent] task: {action.Task}（由 Phase 4 子 Agent 接管）",
                _ => null,
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Hook 动作 {Type} 执行失败", action.Type);
            return null;
        }
    }

    private static async Task<string> ExecShellAsync(HookAction action, IReadOnlyDictionary<string, object?> context, double timeoutSeconds)
    {
        var cmd = TemplateEngine.Render(action.Command, context);
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(isWindows ? "/c" : "-c");
        psi.ArgumentList.Add(cmd);

        using var process = new Process { StartInfo = psi };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync(cts.Token).ConfigureAwait(false);
        await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

        var result = new StringBuilder(stdout);
        if (stderr.Length > 0)
        {
            result.Append($"\n[stderr]\n{stderr}");
        }
        var text = result.ToString();
        return text.Length > 2000 ? text[..2000] : text;
    }

    private async Task<string> ExecHttpAsync(HookAction action, IReadOnlyDictionary<string, object?> context, double timeoutSeconds)
    {
        var url = TemplateEngine.Render(action.Url, context);
        using var request = new HttpRequestMessage(new HttpMethod(action.Method), url);
        if (!string.IsNullOrEmpty(action.Body))
        {
            request.Content = new StringContent(TemplateEngine.Render(action.Body, context));
        }
        foreach (var (k, v) in action.Headers)
        {
            request.Headers.TryAddWithoutValidation(k, v);
        }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var resp = await httpClient.SendAsync(request, cts.Token).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        return text.Length > 2000 ? text[..2000] : text;
    }
}
