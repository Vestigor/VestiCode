using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

/// <summary>
/// Lead 编排器：用 LLM 把目标拆成可并行、按文件边界解耦的子任务并按角色分配
/// → 按分配/依赖增量分派给成员 → 合并各成员 worktree → 在合并结果上做一致性校准 → LLM 综合最终报告。
/// </summary>
public sealed class LeadAgent(
    TeamDef def,
    IReadOnlyDictionary<string, TeamMember> members,
    SharedTaskList tasks,
    GitMerger merger,
    ILlmProvider provider,
    string repoRoot,
    ToolExecutor executor,
    ILoggerFactory loggerFactory)
{
    private const int ReconcileMaxRounds = 24;

    // 计划期分配：任务 Id → 期望成员名（任务本身的 AssignedTo 仅在真正分派时才置位）。
    private readonly Dictionary<string, string> _plannedAssignee = new(StringComparer.Ordinal);
    private Action<string>? _progress; // 运行进度回调（供 TUI 实时显示）

    private void Report(string message) => _progress?.Invoke(message);

    /// <summary>端到端执行一个团队目标。<paramref name="progress"/> 用于上报实时进度。</summary>
    public async Task<string> ExecuteAsync(string goal, Action<string>? progress = null, CancellationToken ct = default)
    {
        _progress = progress;

        Report("Lead 正在拆解目标…");
        await DecomposeAsync(goal, ct).ConfigureAwait(false);
        Report($"已拆解为 {tasks.ListAll().Count} 个子任务，开始分派。");

        await DispatchLoopAsync(ct).ConfigureAwait(false);

        Report("成员已完成，Lead 正在合并各分支…");
        var mergeResults = await MergeAllAsync(ct).ConfigureAwait(false);

        await ReconcileAsync(goal, ct).ConfigureAwait(false);

        Report("正在汇总各成员产出…");
        var synthesis = await SynthesizeAsync(goal, mergeResults, ct).ConfigureAwait(false);

        var report = new StringBuilder();
        report.Append("## Team 执行完成\n\n### 合并结果\n").Append(mergeResults).Append("\n\n");
        if (synthesis.Length > 0)
        {
            report.Append("### Lead 综合报告\n").Append(synthesis).Append("\n\n");
        }
        report.Append($"任务统计: {TaskSummary()}");
        return report.ToString();
    }

    // ---------- 1) LLM 拆解 ----------

    private async Task DecomposeAsync(string goal, CancellationToken ct)
    {
        try
        {
            var roles = new RoleLoader().LoadAll();
            var roster = string.Join("\n", def.Members.Select(m =>
            {
                var desc = roles.GetValueOrDefault(m.Role)?.Description ?? "(无特定角色)";
                return $"- {m.Name}（角色 {(string.IsNullOrEmpty(m.Role) ? "无" : m.Role)}：{desc}）";
            }));

            var leadPrompt = string.IsNullOrEmpty(def.LeadRole)
                ? null
                : roles.GetValueOrDefault(def.LeadRole)?.SystemPrompt;
            // DispatchMode：Lead 以纯指挥官身份工作（本就不碰文件工具），注入 10 阶段工作流。
            var dispatchPrefix = def.DispatchMode ? DispatchScheduler.WorkflowText + "\n\n" : "";
            var system = dispatchPrefix
                + (string.IsNullOrEmpty(leadPrompt) ? "" : leadPrompt + "\n\n")
                + "你是团队 Lead，负责把目标拆解为可并行执行、按文件边界解耦的子任务并分配给成员。";

            var user = $$"""
                团队目标：
                {{goal}}

                成员名单（分配必须符合各成员角色能力）：
                {{roster}}

                当前项目文件（据此按文件边界切分，避免多人改同一文件）：
                {{SnapshotFiles()}}

                只输出如下 JSON（不要任何额外文字、不要 markdown 代码围栏）：
                {
                  "subtasks": [
                    {"id": "t1", "assignee": "成员名", "description": "具体且自足的任务描述", "files": ["将创建或修改的相对路径"], "dependsOn": []}
                  ]
                }

                约束：
                - 每个子任务只分配给一个成员；assignee 必须是上面列出的成员名，且分配要匹配其角色。
                - 不同成员的子任务，files 不得重叠（防止合并冲突）。
                - 若两块工作必须改同一文件，请分配给同一成员，或用 dependsOn 串行化。
                - 子任务数量适中（一般等于或略多于成员数）。
                """;

            var raw = await AskLlmAsync(system, user, ct).ConfigureAwait(false);
            if (!TryBuildTasksFromPlan(raw))
            {
                DecomposeFallback(goal);
            }
        }
        catch (Exception)
        {
            DecomposeFallback(goal); // LLM 不可用/异常时退回朴素拆解，保证可运行
        }
    }

    private bool TryBuildTasksFromPlan(string raw)
    {
        try
        {
            var node = JsonNode.Parse(ExtractJson(raw));
            var arr = node?["subtasks"]?.AsArray();
            if (arr is null || arr.Count == 0)
            {
                return false;
            }

            var memberNames = members.Keys.ToHashSet(StringComparer.Ordinal);
            var idMap = new Dictionary<string, string>(StringComparer.Ordinal); // planId → 真实任务 Id
            var depSpecs = new List<(string realId, JsonArray? deps)>();

            foreach (var item in arr)
            {
                if (item is null)
                {
                    continue;
                }
                var assignee = item["assignee"]?.GetValue<string>() ?? "";
                var desc = item["description"]?.GetValue<string>() ?? "";
                if (!memberNames.Contains(assignee) || string.IsNullOrWhiteSpace(desc))
                {
                    return false; // 计划不合法，整体退回 fallback
                }

                var planId = item["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N")[..6];
                var files = item["files"]?.AsArray()?
                    .Select(f => f?.GetValue<string>())
                    .Where(f => !string.IsNullOrWhiteSpace(f))
                    .ToList();
                var name = files is { Count: > 0 } ? string.Join(",", files!) : planId;

                var task = tasks.Create(name, desc);
                idMap[planId] = task.Id;
                _plannedAssignee[task.Id] = assignee;
                depSpecs.Add((task.Id, item["dependsOn"]?.AsArray()));
            }

            // 第二遍：把计划内的依赖（planId）映射为真实任务 Id。
            foreach (var (realId, deps) in depSpecs)
            {
                if (deps is null)
                {
                    continue;
                }
                var resolved = deps
                    .Select(d => d?.GetValue<string>())
                    .Where(d => d is not null && idMap.ContainsKey(d))
                    .Select(d => idMap[d!])
                    .Where(rid => rid != realId)
                    .ToList();
                if (resolved.Count > 0 && tasks.Get(realId) is { } t)
                {
                    t.DependsOn = resolved;
                    tasks.Update(realId); // 触发持久化
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void DecomposeFallback(string goal)
    {
        // 朴素退路：每个成员一个任务（同一目标），靠各自角色错开产出。
        var i = 0;
        foreach (var name in members.Keys)
        {
            i++;
            var task = tasks.Create($"task-{i}", $"由 {name} 处理: {goal}");
            _plannedAssignee[task.Id] = name;
        }
    }

    // ---------- 2) 分派（尊重计划分配与依赖）----------

    private async Task DispatchLoopAsync(CancellationToken ct)
    {
        var running = new Dictionary<string, Task<string>>(StringComparer.Ordinal);
        var taskOfMember = new Dictionary<string, string>(StringComparer.Ordinal);

        while (true)
        {
            foreach (var task in tasks.ReadyTasks())
            {
                var wanted = _plannedAssignee.GetValueOrDefault(task.Id);
                TeamMember? member;
                if (!string.IsNullOrEmpty(wanted))
                {
                    if (running.ContainsKey(wanted))
                    {
                        continue; // 指定成员正忙，留到其空闲后再派（同成员任务自然串行）
                    }
                    member = members.GetValueOrDefault(wanted)
                             ?? members.Values.FirstOrDefault(m => !running.ContainsKey(m.Name));
                }
                else
                {
                    member = members.Values.FirstOrDefault(m => !running.ContainsKey(m.Name));
                }

                if (member is null)
                {
                    continue; // 无空闲成员可承接
                }

                tasks.Assign(task.Id, member.Name);
                taskOfMember[member.Name] = task.Id;
                running[member.Name] = member.RunAsync(task.Description, ct);
                Report($"▶ 分派给 {member.Name}：{Truncate(task.Description, 50)}");
            }

            var pending = tasks.ListAll(TeamTaskStatus.Pending).Count;
            var inProgress = tasks.ListAll(TeamTaskStatus.InProgress).Count;
            if (pending == 0 && inProgress == 0)
            {
                break;
            }
            if (running.Count == 0)
            {
                break; // 无可推进（依赖死锁等），避免空转
            }

            await Task.WhenAny(running.Values).ConfigureAwait(false);

            foreach (var (memberName, runTask) in running.Where(kv => kv.Value.IsCompleted).ToList())
            {
                var taskId = taskOfMember[memberName];
                if (runTask.IsCompletedSuccessfully)
                {
                    tasks.Complete(taskId, runTask.Result);
                    Report($"✓ {memberName} 完成并已提交分支");
                }
                else
                {
                    tasks.Update(taskId, TeamTaskStatus.Failed, runTask.Exception?.Message ?? "失败");
                    Report($"✗ {memberName} 失败：{runTask.Exception?.Message ?? "未知错误"}");
                }
                running.Remove(memberName);
                taskOfMember.Remove(memberName);
            }
        }

        await Task.WhenAll(running.Values).ConfigureAwait(false);
    }

    // ---------- 合并 ----------

    private async Task<string> MergeAllAsync(CancellationToken ct)
    {
        var results = new StringBuilder();
        foreach (var member in def.Members.Where(m => !string.IsNullOrEmpty(m.Worktree)))
        {
            var branch = "vesticode/" + member.Worktree.Replace('/', '-');
            var (_, msg) = await merger.MergeAsync(branch, ct: ct).ConfigureAwait(false);
            results.Append($"  {member.Name} ({branch}): {msg}\n");
            Report($"⇄ 合并 {member.Name}：{msg}");
        }
        return results.Length == 0 ? "  (无 worktree 需要合并)" : results.ToString().TrimEnd();
    }

    // ---------- 2.5) 合并后一致性校准 ----------

    /// <summary>
    /// 成员是隔离工作的，彼此产出可能不一致（文档与实现脱节、缺解决方案/引用、构建/测试不通过等）。
    /// 合并到 main 后，在 main 上跑一个带工具的单 Agent，通读真实代码，校准所有不一致并提交。
    /// </summary>
    private async Task ReconcileAsync(string goal, CancellationToken ct)
    {
        Report("正在校准合并结果：通读实际代码，对齐文档/构建/引用等不一致…");

        var shell = new PersistentShell(repoRoot);
        var registry = new ToolRegistry();
        registry.Register(new ReadFileTool(repoRoot));
        registry.Register(new WriteFileTool(repoRoot));
        registry.Register(new EditFileTool(repoRoot));
        registry.Register(new GlobTool(repoRoot));
        registry.Register(new GrepTool(repoRoot));
        registry.Register(new RunCommandTool(shell));

        var loop = new AgentLoop(
            provider, registry, executor,
            Options.Create(new AppOptions { Agent = new AgentOptions { MaxRounds = ReconcileMaxRounds } }),
            loggerFactory.CreateLogger<AgentLoop>(),
            promptBuilder: new PromptBuilder());

        var history = new ConversationHistory();
        history.AddUserMessage($$"""
            团队多名成员已各自在隔离环境完成工作，并把产出合并到了当前目录（项目根）。
            因为成员彼此看不到对方的代码，合并结果可能存在不一致。请你**通读当前目录里的真实代码**，
            校准所有不一致，让整个项目自洽、可构建、可测试：

            - **文档对齐实现**：README 及其它文档必须与真实代码一致——项目名、命名空间、目录结构、
              目标框架版本、实际支持的命令与参数、运行/构建/测试命令。删除任何与实现不符或并未实现的内容。
            - **可构建可测试**：补齐缺失的工程文件（如解决方案 .sln、项目间引用），修正错误的路径与引用；
              用 run_command 实际构建并跑测试，确保通过。
            - **范围限制**：只做「对齐与修复」，不要新增目标之外的新功能。

            原始目标（供参考）：{{goal}}

            完成后用一两句话说明你校准/修复了哪些不一致。
            """);

        try
        {
            await foreach (var ev in loop.RunAsync(history, ct).ConfigureAwait(false))
            {
                if (ev is AgentDoneEvent)
                {
                    break;
                }
            }
        }
        catch (Exception)
        {
            // 校准失败不应让整个团队流程崩溃；保留已合并的成员产出。
            Report("校准步骤出错，已跳过（成员产出仍已合并）。");
            return;
        }

        // 把校准产生的改动提交到 main（无改动则 commit 失败，忽略）。
        await TeamGit.RunAsync(repoRoot, ct, "add", "-A").ConfigureAwait(false);
        await TeamGit.RunAsync(repoRoot, ct, "commit", "-m", "team: 校准合并结果（文档/构建/引用对齐实现）").ConfigureAwait(false);
    }

    // ---------- 3) LLM 综合 ----------

    private async Task<string> SynthesizeAsync(string goal, string mergeResults, CancellationToken ct)
    {
        try
        {
            var work = string.Join("\n\n", tasks.ListAll().Select(t =>
                $"### {(string.IsNullOrEmpty(t.AssignedTo) ? "?" : t.AssignedTo)} — {t.Name} [{t.Status}]\n{Truncate(t.Result, 800)}"));

            var system = "你是团队 Lead，负责汇总各成员成果，向用户给出简洁的中文最终报告。";
            var user = $$"""
                团队目标：{{goal}}

                各成员产出：
                {{work}}

                Git 合并结果：
                {{mergeResults}}

                请给出最终报告：1) 总体完成情况；2) 各成员关键产出；3) 风险或未完成项（如有）。简洁，不超过 400 字。
                """;
            return (await AskLlmAsync(system, user, ct).ConfigureAwait(false)).Trim();
        }
        catch (Exception)
        {
            return ""; // 综合失败不影响主流程
        }
    }

    // ---------- 工具方法 ----------

    private async Task<string> AskLlmAsync(string system, string user, CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.FromSystem(system),
            ChatMessage.FromUser(user),
        };
        var sb = new StringBuilder();
        await foreach (var item in provider.ChatStreamAsync(messages, null, ct).ConfigureAwait(false))
        {
            switch (item)
            {
                case TextDelta td:
                    sb.Append(td.Text);
                    break;
                case StreamError err:
                    throw new InvalidOperationException(err.Message);
            }
        }
        return sb.ToString();
    }

    private string SnapshotFiles()
    {
        try
        {
            var skip = new HashSet<string>(StringComparer.Ordinal)
            {
                ".git", ".vesticode", "bin", "obj", "node_modules", ".vs", ".idea",
            };
            var rels = Directory.EnumerateFiles(repoRoot, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(repoRoot, f))
                .Where(r => !r.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(skip.Contains))
                .Take(200)
                .ToList();
            return rels.Count == 0 ? "(空项目，从零创建)" : string.Join("\n", rels);
        }
        catch (Exception)
        {
            return "(无法读取项目文件)";
        }
    }

    private static string ExtractJson(string s)
    {
        var t = s.Trim();
        var start = t.IndexOf('{');
        var end = t.LastIndexOf('}');
        return start >= 0 && end > start ? t[start..(end + 1)] : t;
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

    private string TaskSummary()
    {
        var all = tasks.ListAll();
        var done = all.Count(t => t.Status == TeamTaskStatus.Completed);
        return $"{done}/{all.Count} 完成";
    }
}
