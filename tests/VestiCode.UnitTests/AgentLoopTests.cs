using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VestiCode.Core.Agents;
using VestiCode.Core.Configuration;
using VestiCode.Core.Conversation;
using VestiCode.Core.Llm;
using VestiCode.Core.Tools;
using VestiCode.Core.Tools.Builtin;

namespace VestiCode.UnitTests;

public sealed class AgentLoopTests
{
    [Fact]
    public async Task RunAsync_ToolCall_Then_FinalText_ExecutesToolAndCompletes()
    {
        // 在唯一临时目录里运行，避免污染工作区。
        var tempDir = Directory.CreateTempSubdirectory("vesticode_test_");
        var previousCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(tempDir.FullName);
        try
        {
            // 脚本化 Provider：第 1 轮请求 write_file，第 2 轮给出最终文本。
            var provider = new ScriptedProvider(
            [
                [new ToolCallReady(new ToolCall("call-1", "write_file", new JsonObject
                {
                    ["path"] = "hello.txt",
                    ["content"] = "你好 VestiCode",
                }))],
                [new TextDelta("已为你创建文件。")],
            ]);

            var registry = new ToolRegistry();
            registry.Register(new WriteFileTool());
            var executor = new ToolExecutor(NullLogger<ToolExecutor>.Instance);
            var options = Options.Create(new AppOptions { Agent = new AgentOptions { MaxRounds = 5 } });

            var loop = new AgentLoop(provider, registry, executor, options, NullLogger<AgentLoop>.Instance);

            var history = new ConversationHistory();
            history.AddUserMessage("创建 hello.txt");

            var events = new List<AgentEvent>();
            await foreach (var ev in loop.RunAsync(history))
            {
                events.Add(ev);
            }

            // 工具真正执行了：文件被写入。
            Assert.True(File.Exists(Path.Combine(tempDir.FullName, "hello.txt")));
            Assert.Equal("你好 VestiCode", await File.ReadAllTextAsync(Path.Combine(tempDir.FullName, "hello.txt")));

            // 循环正常结束（模型不再请求工具）。
            var done = Assert.IsType<AgentDoneEvent>(events[^1]);
            Assert.Equal(AgentDoneReason.NoToolCall, done.Reason);

            // 产出了工具调用与工具结果事件各一次。
            Assert.Single(events.OfType<ToolCallEvent>());
            Assert.Single(events.OfType<ToolResultEvent>());
            Assert.Contains(events.OfType<ToolResultEvent>(), e => e.Result.Success);
        }
        finally
        {
            Directory.SetCurrentDirectory(previousCwd);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_NoToolCall_CompletesInOneRound()
    {
        var provider = new ScriptedProvider([[new TextDelta("直接回答，无需工具。")]]);
        var registry = new ToolRegistry();
        var executor = new ToolExecutor(NullLogger<ToolExecutor>.Instance);
        var options = Options.Create(new AppOptions { Agent = new AgentOptions { MaxRounds = 5 } });
        var loop = new AgentLoop(provider, registry, executor, options, NullLogger<AgentLoop>.Instance);

        var history = new ConversationHistory();
        history.AddUserMessage("你好");

        var events = new List<AgentEvent>();
        await foreach (var ev in loop.RunAsync(history))
        {
            events.Add(ev);
        }

        Assert.Empty(events.OfType<ToolCallEvent>());
        var done = Assert.IsType<AgentDoneEvent>(events[^1]);
        Assert.Equal(AgentDoneReason.NoToolCall, done.Reason);
    }

    /// <summary>按轮次脚本化返回流式项的假 Provider。</summary>
    private sealed class ScriptedProvider(IReadOnlyList<IReadOnlyList<LlmStreamItem>> rounds) : ILlmProvider
    {
        private int _round;

        public ProviderOptions Config { get; } = new()
        {
            Name = "fake",
            Protocol = "openai",
            Model = "fake-model",
        };

        public async IAsyncEnumerable<LlmStreamItem> ChatStreamAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var items = _round < rounds.Count ? rounds[_round] : [new TextDelta("(脚本结束)")];
            _round++;
            foreach (var item in items)
            {
                await Task.Yield();
                yield return item;
            }
        }
    }
}
