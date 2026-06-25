using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VestiCode.Core.Agents;
using VestiCode.Core.Configuration;
using VestiCode.Core.Conversation;
using VestiCode.Core.Llm;
using VestiCode.Core.Prompts;
using VestiCode.Core.SubAgents;
using VestiCode.Core.Tools;
using VestiCode.Core.Tools.Builtin;

namespace VestiCode.Core.Teams;

/// <summary>团队成员：在协程中运行，拥有独立历史与团队协作工具。</summary>
public sealed class TeamMember
{
    private readonly MemberDef _def;
    private readonly ILlmProvider _provider;
    private readonly string _workspaceDir; // 成员独占的 git worktree 目录
    private readonly ToolExecutor _executor;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SharedTaskList _tasks;
    private readonly Mailbox _mailbox;
    private readonly IReadOnlyList<string> _allMembers;
    private readonly int _maxRounds;
    private readonly System.Net.Http.IHttpClientFactory _httpClientFactory;
    private readonly SubAgentRole? _role;
    private readonly ConversationHistory _history = new();
    private string _lastMsgId = "";

    public TeamMember(
        MemberDef def, string teamDir, ILlmProvider provider, string workspaceDir,
        ToolExecutor executor, ILoggerFactory loggerFactory, SharedTaskList tasks,
        IReadOnlyList<string> allMembers, int maxRounds,
        System.Net.Http.IHttpClientFactory httpClientFactory)
    {
        _def = def;
        _provider = provider;
        _workspaceDir = workspaceDir;
        _executor = executor;
        _loggerFactory = loggerFactory;
        _tasks = tasks;
        _mailbox = new Mailbox(teamDir, def.Name);
        _allMembers = allMembers;
        _maxRounds = maxRounds;
        _httpClientFactory = httpClientFactory;
        // 成员角色：三级加载（内置 → 全局 → 项目），用其 SystemPrompt 与工具白名单约束成员行为。
        _role = string.IsNullOrEmpty(def.Role) ? null : new RoleLoader().LoadAll().GetValueOrDefault(def.Role);
    }

    public string Name => _def.Name;
    public MemberStatus Status { get; private set; } = MemberStatus.Idle;

    /// <summary>执行一个任务到完成，返回结果文本。</summary>
    public async Task<string> RunAsync(string task, CancellationToken ct = default)
    {
        Status = MemberStatus.Busy;
        CheckMail();
        // 角色 SystemPrompt 注入到任务消息前，使成员按角色行事（参考 SubAgentRunner）。
        _history.AddUserMessage(_role is null ? task : $"{_role.SystemPrompt}\n\n任务: {task}");

        // 工具全部根植于成员自己的 worktree（文件操作 + shell 都在隔离目录内进行）。
        var shell = new PersistentShell(_workspaceDir);
        var workTools = new List<ITool>
        {
            new ReadFileTool(_workspaceDir),
            new WriteFileTool(_workspaceDir),
            new EditFileTool(_workspaceDir),
            new GlobTool(_workspaceDir),
            new GrepTool(_workspaceDir),
            new RunCommandTool(shell),
            new WebFetchTool(_httpClientFactory),
        };

        // 角色的 allow/deny 只过滤工作工具；团队协作工具（任务清单/邮箱）始终保留。
        IEnumerable<ITool> selectedWork = workTools;
        if (_role is not null)
        {
            var allowed = ToolFilter.Filter(workTools.Select(t => t.Name), _role).ToHashSet(StringComparer.Ordinal);
            selectedWork = workTools.Where(t => allowed.Contains(t.Name));
        }

        var registry = new ToolRegistry();
        registry.RegisterRange(selectedWork);
        registry.RegisterRange(TeamTools.Create(_tasks, _mailbox, _def.Name, _allMembers));

        var loop = new AgentLoop(
            _provider, registry, _executor,
            Options.Create(new AppOptions { Agent = new AgentOptions { MaxRounds = _maxRounds } }),
            _loggerFactory.CreateLogger<AgentLoop>(),
            promptBuilder: new PromptBuilder());

        var sb = new StringBuilder();
        try
        {
            await foreach (var ev in loop.RunAsync(_history, ct).ConfigureAwait(false))
            {
                if (ev is TextDeltaEvent td)
                {
                    sb.Append(td.Text);
                }
                else if (ev is AgentDoneEvent)
                {
                    break;
                }
            }
        }
        finally
        {
            shell.Dispose();
        }

        // 把成员在 worktree 里的改动提交到其分支，供 Lead 合并（无改动则 commit 失败，忽略）。
        await TeamGit.RunAsync(_workspaceDir, ct, "add", "-A").ConfigureAwait(false);
        await TeamGit.RunAsync(_workspaceDir, ct, "commit", "-m", $"team({_def.Name}): 成员产出").ConfigureAwait(false);

        Status = MemberStatus.Idle;
        NotifyLead("done", sb.ToString());
        return sb.ToString();
    }

    private void NotifyLead(string evt, string detail)
        => _mailbox.Send(new TeamMessage
        {
            From = _def.Name,
            To = "lead",
            Type = MessageType.Lifecycle,
            Content = $"[{evt}] {detail}",
        });

    private void CheckMail()
    {
        var msgs = _mailbox.ReadNew(_lastMsgId);
        if (msgs.Count == 0)
        {
            return;
        }
        _lastMsgId = msgs[^1].Id;
        foreach (var m in msgs.Where(m => m.Type == MessageType.Text && m.From == "lead"))
        {
            _history.AddContextMessage($"[Lead → {_def.Name}]: {m.Content}");
        }
    }
}
