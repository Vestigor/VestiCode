using Microsoft.Extensions.Logging.Abstractions;
using VestiCode.Core.Hooks;

namespace VestiCode.UnitTests;

public sealed class HookTests
{
    [Fact]
    public void Template_SubstitutesNestedVars()
    {
        var ctx = new Dictionary<string, object?>
        {
            ["tool_name"] = "run_command",
            ["params"] = new Dictionary<string, object?> { ["command"] = "rm -rf /" },
        };
        Assert.Equal("工具 run_command 命令 rm -rf /",
            TemplateEngine.Render("工具 {{tool_name}} 命令 {{params.command}}", ctx));
        Assert.Equal("未定义: ", TemplateEngine.Render("未定义: {{missing}}", ctx));
    }

    [Fact]
    public void Condition_AllAndRegex()
    {
        var condition = new HookCondition
        {
            Match = MatchMode.All,
            Rules =
            [
                new ConditionRule { Field = "tool_name", Operator = HookOperator.Exact, Value = "run_command" },
                new ConditionRule { Field = "params.command", Operator = HookOperator.Regex, Value = @"rm\s+-rf" },
            ],
        };
        var match = new Dictionary<string, object?>
        {
            ["tool_name"] = "run_command",
            ["params"] = new Dictionary<string, object?> { ["command"] = "rm -rf /tmp" },
        };
        var noMatch = new Dictionary<string, object?>
        {
            ["tool_name"] = "run_command",
            ["params"] = new Dictionary<string, object?> { ["command"] = "ls -la" },
        };
        Assert.True(ConditionEvaluator.Evaluate(condition, match));
        Assert.False(ConditionEvaluator.Evaluate(condition, noMatch));
    }

    [Fact]
    public async Task Engine_InterceptRule_ReturnsRejection()
    {
        var rule = new HookRule
        {
            Event = HookEvent.ToolPreExec,
            Actions = [new HookAction { Type = ActionType.PromptInject, Text = "拦截 {{params.command}}" }],
            Name = "block",
        };
        using var http = new HttpClient();
        var engine = new HookEngine([rule], new ActionExecutor(NullLogger<ActionExecutor>.Instance, http));

        var rejection = await engine.FireAsync(HookEvent.ToolPreExec, new Dictionary<string, object?>
        {
            ["tool_name"] = "run_command",
            ["params"] = new Dictionary<string, object?> { ["command"] = "rm -rf /" },
        });

        Assert.Equal("拦截 rm -rf /", rejection);
    }

    [Fact]
    public void Loader_ParsesYaml_AndRejectsAsyncIntercept()
    {
        // 直接测 YAML 解析/校验逻辑（Hook 现仅从内置嵌入资源加载，不读文件）。
        var rules = new HookLoader(NullLogger<HookLoader>.Instance).LoadFromText(
            """
            hooks:
              - name: block-rm
                event: tool_pre_exec
                condition:
                  match: ALL
                  rules:
                    - field: params.command
                      operator: regex
                      value: "rm\\s+-rf"
                actions:
                  - type: prompt_inject
                    text: "危险命令被拦截"
              - name: bad-async-intercept
                event: tool_pre_exec
                actions:
                  - type: prompt_inject
                    text: "x"
                control:
                  async: true
            """);

        // 第二条（async 拦截）应被校验拒绝，只剩第一条。
        Assert.Single(rules);
        Assert.Equal("block-rm", rules[0].Name);
        Assert.True(rules[0].IsIntercept);
    }
}
