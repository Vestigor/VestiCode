using System.Text.Json;
using System.Text.Json.Serialization;

namespace VestiCode.Core.Teams;

/// <summary>成员邮箱：追加式 JSONL，点对点 + 广播。</summary>
public sealed class Mailbox
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _dir;
    private readonly string _file;

    public Mailbox(string teamDir, string memberName)
    {
        _dir = Path.Combine(teamDir, "mailboxes");
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, $"{memberName}.jsonl");
    }

    public void Send(TeamMessage msg)
        => File.AppendAllText(_file, JsonSerializer.Serialize(msg, JsonOptions) + "\n");

    /// <summary>读取 <paramref name="sinceId"/> 之后的新消息（空 = 全部）。</summary>
    public IReadOnlyList<TeamMessage> ReadNew(string sinceId = "")
    {
        if (!File.Exists(_file))
        {
            return [];
        }
        var messages = new List<TeamMessage>();
        var foundSince = string.IsNullOrEmpty(sinceId);
        foreach (var line in File.ReadLines(_file))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            TeamMessage? msg;
            try
            {
                msg = JsonSerializer.Deserialize<TeamMessage>(line, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }
            if (msg is null)
            {
                continue;
            }
            if (!foundSince)
            {
                if (msg.Id == sinceId)
                {
                    foundSince = true;
                }
                continue;
            }
            messages.Add(msg);
        }
        return messages;
    }

    /// <summary>把消息发给除发送者外的所有成员邮箱。</summary>
    public void Broadcast(TeamMessage msg, IEnumerable<string> allMembers)
    {
        foreach (var name in allMembers)
        {
            if (name == msg.From)
            {
                continue;
            }
            new Mailbox(Path.GetDirectoryName(_dir)!, name).Send(new TeamMessage
            {
                From = msg.From,
                To = name,
                Type = msg.Type,
                Content = msg.Content,
                Summary = msg.Summary,
            });
        }
    }
}
