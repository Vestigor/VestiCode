using VestiCode.Core.Conversation;
using VestiCode.Core.Llm;
using VestiCode.Core.Prompts;

namespace VestiCode.UnitTests;

public sealed class PromptAndCompressionTests
{
    [Fact]
    public void PromptBuilder_LoadsModules_InPriorityOrder()
    {
        var prompt = new PromptBuilder().Build();

        Assert.Contains("VestiCode", prompt);
        Assert.Contains("## 行为准则", prompt);
        Assert.Contains("## 工具使用", prompt);
        Assert.Contains("## 输出风格", prompt);

        // 身份模块（01）必须排在行为模块（02）之前。
        Assert.True(prompt.IndexOf("终端中的 AI 编程助手", StringComparison.Ordinal)
                    < prompt.IndexOf("## 行为准则", StringComparison.Ordinal));
    }

    [Fact]
    public void PromptInjector_PlanOnly_FullOnFirstRound()
    {
        var injector = new PromptInjector();
        injector.SetPlanOnly(true);
        var first = injector.BuildInjection(1);
        Assert.NotNull(first);
        Assert.Contains("plan-only", first);
    }

    [Fact]
    public void PromptInjector_OneShot_OnlyNextRound()
    {
        var injector = new PromptInjector();
        injector.QueueInjection("记得运行测试");
        Assert.Contains("记得运行测试", injector.BuildInjection(1));
        Assert.Null(injector.BuildInjection(2)); // 一次性，第二轮已清空
    }

    [Fact]
    public void Truncator_OversizedToolResult_WrittenToDiskWithPreview()
    {
        var dir = Directory.CreateTempSubdirectory("vesticode_tr_");
        try
        {
            var cfg = new TruncateConfig { PerResultThreshold = 100, PreviewLength = 20, StorageDir = dir.FullName };
            var truncator = new ToolResultTruncator(cfg);

            var big = new string('x', 500);
            var messages = new List<ChatMessage> { ChatMessage.FromToolResult("c1", "read_file", big) };

            var (processed, infos) = truncator.ProcessRound(messages);

            Assert.Single(infos);
            Assert.Equal(500, infos[0].OriginalChars);
            Assert.True(File.Exists(infos[0].FilePath));
            Assert.Contains("完整内容已保存到磁盘", processed[0].Text);
            Assert.True(processed[0].Text!.Length < big.Length);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Summarizer_EstimateTokens_GrowsWithContent()
    {
        var small = new List<ChatMessage> { ChatMessage.FromUser("hi") };
        var large = new List<ChatMessage> { ChatMessage.FromUser(new string('a', 7000)) };
        Assert.True(StructuredSummarizer.EstimateTokens(large) > StructuredSummarizer.EstimateTokens(small));
    }

    [Fact]
    public void Summarizer_TwoTier_WarnsAt70_CompressesAt90()
    {
        // gpt-3.5 窗口 16385 → 警告 ~11469、压缩 ~14746。
        var s = new StructuredSummarizer(new ThrowingProvider(), "gpt-3.5");
        Assert.True(s.WarnThreshold < s.CompressThreshold);

        // 中等体量：越过 70% 警告线，但未到 90% 压缩线。
        var mid = new List<ChatMessage> { ChatMessage.FromUser(new string('a', 45_000)) };
        Assert.True(s.NeedsWarning(mid));
        Assert.False(s.NeedsCompression(mid));

        // 大体量：两条线都越过。
        var big = new List<ChatMessage> { ChatMessage.FromUser(new string('a', 60_000)) };
        Assert.True(s.NeedsWarning(big));
        Assert.True(s.NeedsCompression(big));
    }

    /// <summary>不会被调用的占位 Provider（阈值测试不触发实际摘要）。</summary>
    private sealed class ThrowingProvider : ILlmProvider
    {
        public VestiCode.Core.Configuration.ProviderOptions Config => new();

        public IAsyncEnumerable<LlmStreamItem> ChatStreamAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<VestiCode.Core.Tools.ToolDefinition>? tools = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
