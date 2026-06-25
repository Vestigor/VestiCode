using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace VestiCode.Core.Hooks;

/// <summary>
/// 从 YAML 加载 Hook 规则并集中校验：项目 <c>./.vesticode/hooks.yaml</c> +
/// 全局 <c>~/.vesticode/hooks.yaml</c>（两者都生效）。无内置默认：没有配置文件即没有规则。
/// 非法规则跳过并记日志。
/// </summary>
public sealed class HookLoader(ILogger<HookLoader> logger)
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static string ProjectPath =>
        Path.Combine(Directory.GetCurrentDirectory(), ".vesticode", "hooks.yaml");

    private static string GlobalPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vesticode", "hooks.yaml");

    public IReadOnlyList<HookRule> Load()
    {
        var rules = new List<HookRule>();
        rules.AddRange(LoadFile(GlobalPath));
        rules.AddRange(LoadFile(ProjectPath));
        return rules;
    }

    private IReadOnlyList<HookRule> LoadFile(string path)
        => File.Exists(path) ? LoadFromText(File.ReadAllText(path)) : [];

    /// <summary>从 YAML 文本解析规则（内置资源用；非法规则跳过并记日志）。</summary>
    public IReadOnlyList<HookRule> LoadFromText(string yaml)
    {
        HooksFile? file;
        try
        {
            file = Yaml.Deserialize<HooksFile>(yaml);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Hook YAML 解析失败");
            return [];
        }

        var rules = new List<HookRule>();
        var index = 0;
        foreach (var dto in file?.Hooks ?? [])
        {
            try
            {
                rules.Add(ParseRule(dto, index));
            }
            catch (Exception ex)
            {
                logger.LogWarning("Hook 规则 #{Index} 无效: {Msg}", index, ex.Message);
            }
            index++;
        }
        return rules;
    }

    private static HookRule ParseRule(RuleDto dto, int index)
    {
        // 错误信息统一带上规则定位，便于在 YAML 中快速找到出错项。
        var where = $"规则 #{index + 1}{(string.IsNullOrEmpty(dto.Name) ? "" : $" '{dto.Name}'")}";

        if (string.IsNullOrEmpty(dto.Event) || !TryParseEnumSnake<HookEvent>(dto.Event, out var evt))
        {
            throw new ArgumentException($"{where}: 无效或缺失 event: {dto.Event}");
        }

        HookCondition? condition = null;
        if (dto.Condition is { } c)
        {
            var match = TryParseEnumSnake<MatchMode>(c.Match ?? "all", out var m) ? m : MatchMode.All;
            var condRules = (c.Rules ?? []).Select(r =>
            {
                if (!TryParseEnumSnake<HookOperator>(r.Operator ?? "exact", out var op))
                {
                    throw new ArgumentException($"{where}: 无效 operator: {r.Operator}");
                }
                return new ConditionRule { Field = r.Field ?? "", Operator = op, Value = r.Value?.ToString() ?? "" };
            }).ToList();
            condition = new HookCondition { Match = match, Rules = condRules };
        }

        var actions = (dto.Actions ?? []).Select(a =>
        {
            if (!TryParseEnumSnake<ActionType>(a.Type ?? "", out var atype))
            {
                throw new ArgumentException($"{where}: 无效 action type: {a.Type}");
            }
            var action = new HookAction
            {
                Type = atype,
                Command = a.Command ?? "",
                Text = a.Text ?? "",
                Url = a.Url ?? "",
                Method = a.Method ?? "POST",
                Headers = a.Headers ?? new Dictionary<string, string>(),
                Body = a.Body ?? "",
                Task = a.Task ?? "",
            };
            ValidateAction(action);
            return action;
        }).ToList();

        if (actions.Count == 0)
        {
            throw new ArgumentException($"{where}: 至少需要一个 action");
        }

        var control = new HookControl
        {
            Once = dto.Control?.Once ?? false,
            Async = dto.Control?.Async ?? false,
            Timeout = dto.Control?.Timeout ?? 30.0,
        };

        if (evt == HookEvent.ToolPreExec && control.Async)
        {
            throw new ArgumentException($"{where}: 拦截事件 tool_pre_exec 不允许 async=true");
        }

        return new HookRule
        {
            Event = evt,
            Condition = condition,
            Actions = actions,
            Control = control,
            Name = string.IsNullOrEmpty(dto.Name) ? $"rule-{index}" : dto.Name,
        };
    }

    private static void ValidateAction(HookAction action)
    {
        switch (action.Type)
        {
            case ActionType.Shell when string.IsNullOrEmpty(action.Command):
                throw new ArgumentException("shell 动作缺少 command");
            case ActionType.PromptInject when string.IsNullOrEmpty(action.Text):
                throw new ArgumentException("prompt_inject 动作缺少 text");
            case ActionType.Http when string.IsNullOrEmpty(action.Url):
                throw new ArgumentException("http 动作缺少 url");
        }
    }

    /// <summary>把 snake_case（如 tool_pre_exec / prompt_inject）解析为枚举。</summary>
    private static bool TryParseEnumSnake<TEnum>(string value, out TEnum result) where TEnum : struct
        => Enum.TryParse(value.Replace("_", ""), ignoreCase: true, out result);

    // -- YAML DTO --------------------------------------------------------------

    private sealed class HooksFile
    {
        public List<RuleDto>? Hooks { get; set; }
    }

    private sealed class RuleDto
    {
        public string? Name { get; set; }
        public string? Event { get; set; }
        public ConditionDto? Condition { get; set; }
        public List<ActionDto>? Actions { get; set; }
        public ControlDto? Control { get; set; }
    }

    private sealed class ConditionDto
    {
        public string? Match { get; set; }
        public List<CondRuleDto>? Rules { get; set; }
    }

    private sealed class CondRuleDto
    {
        public string? Field { get; set; }
        public string? Operator { get; set; }
        public object? Value { get; set; }
    }

    private sealed class ActionDto
    {
        public string? Type { get; set; }
        public string? Command { get; set; }
        public string? Text { get; set; }
        public string? Url { get; set; }
        public string? Method { get; set; }
        public Dictionary<string, string>? Headers { get; set; }
        public string? Body { get; set; }
        public string? Task { get; set; }
    }

    private sealed class ControlDto
    {
        public bool Once { get; set; }
        public bool Async { get; set; }
        public double Timeout { get; set; } = 30.0;
    }
}
