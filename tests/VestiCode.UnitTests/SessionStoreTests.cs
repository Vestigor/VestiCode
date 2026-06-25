using System.Text.Json.Nodes;
using VestiCode.Core.Conversation;
using VestiCode.Core.Llm;
using VestiCode.Core.Memory;

namespace VestiCode.UnitTests;

public sealed class SessionStoreTests
{
    [Fact]
    public void SaveThenLoad_RoundTripsMessages()
    {
        var dir = Directory.CreateTempSubdirectory("vesticode_sessions_");
        try
        {
            var store = new JsonlSessionStore(dir.FullName);
            store.NewSession();

            var history = new ConversationHistory();
            history.AddUserMessage("创建文件");
            history.AddRawMessage(ChatMessage.FromToolCalls(
                [new ToolCall("c1", "write_file", new JsonObject { ["path"] = "a.txt" })], "我来创建"));
            history.AddRawMessage(ChatMessage.FromToolResult("c1", "write_file", "已写入"));
            history.AddAssistantMessage("完成");

            store.Save(history, "deepseek", "deepseek-chat");

            var loaded = new JsonlSessionStore(dir.FullName).Load();
            Assert.NotNull(loaded);
            var (restored, provider, model) = loaded!.Value;

            Assert.Equal("deepseek", provider);
            Assert.Equal("deepseek-chat", model);
            var msgs = restored.GetMessages();
            Assert.Equal(4, msgs.Count);
            Assert.Equal(ChatRole.User, msgs[0].Role);
            Assert.Single(msgs[1].ToolCalls);
            Assert.Equal("write_file", msgs[1].ToolCalls[0].Name);
            Assert.Equal(ChatRole.Tool, msgs[2].Role);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Load_SkipsCorruptLines()
    {
        var dir = Directory.CreateTempSubdirectory("vesticode_sessions_");
        try
        {
            var store = new JsonlSessionStore(dir.FullName);
            var sid = store.NewSession();
            var history = new ConversationHistory();
            history.AddUserMessage("你好");
            history.AddAssistantMessage("你好！");
            store.Save(history, "deepseek", "deepseek-chat");

            // 追加一行损坏 JSON，模拟崩溃。
            File.AppendAllText(Path.Combine(dir.FullName, $"{sid}.jsonl"), "{ this is not valid json\n");

            var loaded = new JsonlSessionStore(dir.FullName).Load(sid);
            Assert.NotNull(loaded);
            Assert.Equal(2, loaded!.Value.History.GetMessages().Count);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Load_TruncatesUnpairedToolCall()
    {
        var dir = Directory.CreateTempSubdirectory("vesticode_sessions_");
        try
        {
            var store = new JsonlSessionStore(dir.FullName);
            store.NewSession();
            var history = new ConversationHistory();
            history.AddUserMessage("做事");
            // assistant 请求工具，但没有对应的 tool 结果（模拟中断）。
            history.AddRawMessage(ChatMessage.FromToolCalls(
                [new ToolCall("x1", "read_file", new JsonObject { ["path"] = "a" })], null));
            store.Save(history, "deepseek", "deepseek-chat");

            var loaded = new JsonlSessionStore(dir.FullName).Load();
            Assert.NotNull(loaded);
            // 未配对的 assistant tool_use 之后被截断 → 只剩 user 消息。
            var msgs = loaded!.Value.History.GetMessages();
            Assert.Single(msgs);
            Assert.Equal(ChatRole.User, msgs[0].Role);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
