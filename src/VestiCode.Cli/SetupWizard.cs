using System.Text.Json;
using System.Text.Json.Nodes;
using Spectre.Console;

namespace VestiCode.Cli;

/// <summary>
/// 首次运行配置向导：检测到当前激活 Provider 没有 API Key 时，在终端里引导用户
/// 选择 Provider / 模型 / 填写 Key，写入 <c>~/.vesticode/appsettings.json</c>（用户可后续直接编辑）。
/// </summary>
public static class SetupWizard
{
    // 各 Provider 的默认模型（协议与基址由 Name 推导，配置不含 base_url）。
    private static readonly Dictionary<string, string> Defaults = new(StringComparer.Ordinal)
    {
        ["deepseek"] = "deepseek-chat",
        ["openai"] = "gpt-4o",
        ["anthropic"] = "claude-sonnet-4-6",
    };

    /// <summary>当前激活 Provider 是否缺少可用的 API Key。</summary>
    public static bool NeedsSetup(string? activeProvider, string? activeApiKey)
        => string.IsNullOrWhiteSpace(activeProvider) || string.IsNullOrWhiteSpace(activeApiKey);

    /// <summary>运行向导并把配置写入 <paramref name="userConfigPath"/>。返回是否成功配置。</summary>
    public static bool Run(string userConfigPath)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold magenta]✻ 欢迎使用 VestiCode[/]");
        AnsiConsole.MarkupLine("[dim]首次使用需要配置一个大模型 Provider。配置会保存到你的用户目录，之后可随时修改。[/]");
        AnsiConsole.WriteLine();

        var name = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("选择 [green]Provider[/]：")
                .AddChoices("deepseek", "openai", "anthropic"));

        var defaultModel = Defaults[name];

        var model = AnsiConsole.Prompt(
            new TextPrompt<string>($"模型名 [dim](默认 {defaultModel})[/]：")
                .DefaultValue(defaultModel)
                .ShowDefaultValue(false));

        var apiKey = AnsiConsole.Prompt(
            new TextPrompt<string>("API Key：")
                .Secret()
                .Validate(k => string.IsNullOrWhiteSpace(k)
                    ? ValidationResult.Error("API Key 不能为空")
                    : ValidationResult.Success()));

        Write(userConfigPath, name, model, apiKey);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]✓[/] 配置已保存到 [underline]{userConfigPath}[/]");
        AnsiConsole.MarkupLine($"[dim]  Provider: {name} · Model: {model} · API Key: {Mask(apiKey)}[/]");
        AnsiConsole.MarkupLine("[dim]以后可直接编辑该文件，或用环境变量 VESTICODE_API_KEY 覆盖 Key。[/]");
        AnsiConsole.WriteLine();
        return true;
    }

    /// <summary>API Key 脱敏：保留首 8 位与末 4 位，中间打码（如 sk-c6a56***…***593a）。</summary>
    public static string Mask(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return "(空)";
        }
        return key.Length <= 12
            ? new string('*', key.Length)
            : key[..8] + new string('*', key.Length - 12) + key[^4..];
    }

    /// <summary>写入最小用户配置（只含 Name/Model/ApiKey），并设为激活 Provider。</summary>
    private static void Write(string path, string name, string model, string apiKey)
    {
        var root = new JsonObject
        {
            ["VestiCode"] = new JsonObject
            {
                ["ActiveProvider"] = name,
                ["Providers"] = new JsonArray
                {
                    new JsonObject { ["Name"] = name, ["Model"] = model, ["ApiKey"] = apiKey },
                },
            },
        };

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
