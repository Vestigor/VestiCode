using System.Text.Json;
using System.Text.Json.Serialization;

namespace VestiCode.Core.Security;

/// <summary>
/// 安全策略：按 会话 &gt; 项目 &gt; 全局 的优先级评估规则，未命中则用档位默认值。
/// 持久化规则用 JSON（.NET 原生），存项目级 <c>.vesticode/security.json</c> 与全局
/// <c>~/.vesticode/security.json</c>。
/// </summary>
public sealed class SecurityPolicy
{
    // 严格模式下默认放行的路径 glob。
    private static readonly string[] StrictDefaultAllowed =
    [
        "*.cs", "*.py", "*.txt", "*.md", "*.yaml", "*.yml", "*.json", "*.toml", "*.cfg",
        "*.js", "*.ts", "*.tsx", "*.jsx", "*.css", "*.html",
        "src/**", "lib/**", "tests/**", "test/**", "docs/**",
    ];

    private static readonly HashSet<string> ReadTools = new(StringComparer.Ordinal)
    {
        "read_file", "glob", "grep",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _projectRoot;
    private readonly List<SecurityRule> _sessionRules = [];
    private List<SecurityRule> _projectRules;
    private readonly List<SecurityRule> _globalRules;

    public SecurityPolicy(SecurityLevel level = SecurityLevel.Normal, string? projectRoot = null)
    {
        Level = level;
        _projectRoot = Path.GetFullPath(projectRoot ?? Directory.GetCurrentDirectory());
        _projectRules = LoadRulesFile(ProjectConfigPath);
        _globalRules = LoadRulesFile(GlobalConfigPath);
    }

    public SecurityLevel Level { get; private set; }

    public void SetLevel(SecurityLevel level) => Level = level;

    // -- 规则管理 --------------------------------------------------------------

    public void AddSessionRule(SecurityRule rule)
    {
        rule.Scope = RuleScope.Session;
        _sessionRules.Insert(0, rule);
    }

    public void AddPermanentRule(SecurityRule rule)
    {
        rule.Scope = RuleScope.Project;
        _projectRules.Insert(0, rule);
        SaveProjectRules();
    }

    // -- 评估 ------------------------------------------------------------------

    /// <summary>评估并返回有效动作（会话 &gt; 项目 &gt; 全局 &gt; 档位默认）。</summary>
    /// <param name="isRead">工具是否为只读类（由调用方按工具 Category 传入）。</param>
    public RuleAction Evaluate(string toolName, string? path = null, string? command = null, bool isRead = false)
    {
        foreach (var ruleSet in new[] { _sessionRules, _projectRules, _globalRules })
        {
            foreach (var rule in ruleSet)
            {
                if (rule.Matches(toolName, path, command))
                {
                    return rule.Action;
                }
            }
        }
        return ModeDefault(toolName, path, isRead);
    }

    private const int MaxHitlLines = 80; // 单个参数最多展示行数（极端长内容才截断）

    public string ToHitlPrompt(string toolName, IReadOnlyDictionary<string, string> args)
    {
        var sb = new System.Text.StringBuilder($"⚠ 安全确认: {toolName}");
        foreach (var (key, value) in args)
        {
            var lines = value.Replace("\r\n", "\n").Split('\n');
            if (lines.Length == 1)
            {
                sb.Append($"\n    {key} = {value}");
            }
            else
            {
                // 多行值（如 edit_file 的 old_string/new_string）：标签独占一行，内容逐行缩进，保留换行。
                sb.Append($"\n    {key}:");
                var shown = Math.Min(lines.Length, MaxHitlLines);
                for (var i = 0; i < shown; i++)
                {
                    sb.Append($"\n      {lines[i]}");
                }
                if (lines.Length > shown)
                {
                    sb.Append($"\n      … （还有 {lines.Length - shown} 行）");
                }
            }
        }
        sb.Append($"\n  当前模式: {Level}");
        sb.Append("\n  [A]llow once  [S]ession allow  [P]ermanent allow  [D]eny");
        return sb.ToString();
    }

    /// <summary>把 HITL 决策转化为规则（永久/会话），其余返回 null。</summary>
    public SecurityRule? HitlToRule(HitlDecision decision, string toolName, string? path, string? command)
    {
        // run_command 泛化为「程序+子命令」glob，使同类命令以后不再重复询问（而非只匹配原文）。
        var commandPattern = command is null ? null : ShellCommand.Generalize(command);
        switch (decision)
        {
            case HitlDecision.AllowPermanent:
                var permanent = new SecurityRule
                {
                    Tool = toolName,
                    Action = RuleAction.Allow,
                    PathPattern = path,
                    CommandPattern = commandPattern,
                    Scope = RuleScope.Project,
                };
                AddPermanentRule(permanent);
                return permanent;

            case HitlDecision.AllowSession:
                var session = new SecurityRule
                {
                    Tool = toolName,
                    Action = RuleAction.Allow,
                    PathPattern = path,
                    CommandPattern = commandPattern,
                    Scope = RuleScope.Session,
                };
                AddSessionRule(session);
                return session;

            default:
                return null;
        }
    }

    // -- 内部 ------------------------------------------------------------------

    private RuleAction ModeDefault(string toolName, string? path, bool isRead)
    {
        isRead = isRead || ReadTools.Contains(toolName); // 内置只读工具兜底
        return Level switch
        {
            SecurityLevel.Strict => path is not null && IsPathAllowed(path)
                ? RuleAction.Allow
                : RuleAction.Ask,
            SecurityLevel.Normal => isRead ? RuleAction.Allow : RuleAction.Ask,
            SecurityLevel.Permissive => RuleAction.Allow,
            _ => RuleAction.Ask,
        };
    }

    private bool IsPathAllowed(string path)
    {
        if (StrictDefaultAllowed.Any(p => Glob.IsMatch(p, path)))
        {
            return true;
        }
        foreach (var ruleSet in new[] { _projectRules, _globalRules })
        {
            foreach (var rule in ruleSet)
            {
                if (rule.Action == RuleAction.Allow && rule.PathPattern is not null
                    && Glob.IsMatch(rule.PathPattern, path))
                {
                    return true;
                }
            }
        }
        return false;
    }

    // -- 持久化 ----------------------------------------------------------------

    private string ProjectConfigPath => Path.Combine(_projectRoot, ".vesticode", "security.json");

    private static string GlobalConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vesticode", "security.json");

    private static List<SecurityRule> LoadRulesFile(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }
        try
        {
            var dto = JsonSerializer.Deserialize<RulesFileDto>(File.ReadAllText(path), JsonOptions);
            return dto?.Rules.Select(r => r.ToRule()).ToList() ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private void SaveProjectRules()
    {
        var dto = new RulesFileDto
        {
            Rules = _projectRules.Select(RuleDto.FromRule).ToList(),
        };
        var dir = Path.GetDirectoryName(ProjectConfigPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(ProjectConfigPath, JsonSerializer.Serialize(dto, JsonOptions));
    }

    private sealed class RulesFileDto
    {
        public List<RuleDto> Rules { get; set; } = [];
    }

    private sealed class RuleDto
    {
        public string Tool { get; set; } = "*";
        public string Action { get; set; } = "ask";
        public string? PathPattern { get; set; }
        public string? CommandPattern { get; set; }

        public SecurityRule ToRule() => new()
        {
            Tool = Tool,
            Action = Enum.TryParse<RuleAction>(Action, ignoreCase: true, out var a) ? a : RuleAction.Ask,
            PathPattern = PathPattern,
            CommandPattern = CommandPattern,
            Scope = RuleScope.Project,
        };

        public static RuleDto FromRule(SecurityRule rule) => new()
        {
            Tool = rule.Tool,
            Action = rule.Action.ToString().ToLowerInvariant(),
            PathPattern = rule.PathPattern,
            CommandPattern = rule.CommandPattern,
        };
    }
}
