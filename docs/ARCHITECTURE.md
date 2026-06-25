# VestiCode 架构设计文档

> 基于 .NET 10 的自主编码 AI Agent。核心是「思考(Thought) → 行动(Action) → 观察(Observation)」的 ReAct 推理循环，运行于终端。
>
> 本文档包含 **Agent 架构图、工具设计、推理流程图** 三大部分，并展开记忆、安全、Provider、并发模型、多 Agent 等子系统设计。所有结构图均以 HTML 绘制。

---

## 1. 项目概述

**VestiCode** 是一个运行在终端里的通用编码 Agent。用户用自然语言下达编程任务，Agent 通过反复「调用工具 → 观察结果 → 继续推理」自主完成任务：读写文件、执行命令、搜索代码、联网抓取等。

<table>
<tr><th align="left">维度</th><th align="left">实现</th></tr>
<tr><td>语言 / 运行时</td><td>C# / .NET 10（<code>net10.0</code>），<code>Nullable</code> 与 <code>TreatWarningsAsErrors</code> 全开</td></tr>
<tr><td>LLM 接入</td><td>HTTP 直连 + SSE 流式解析，适配 OpenAI / Anthropic 两套线缆协议（DeepSeek 走 OpenAI 兼容协议）</td></tr>
<tr><td>交互界面</td><td>控制台 TUI：行内流式渲染、Markdown、状态行、人在回路(HITL)弹窗</td></tr>
<tr><td>架构风格</td><td>Clean Architecture：领域核心（Core）与界面（Cli）彻底解耦，经由事件流通信</td></tr>
<tr><td>核心能力</td><td>ReAct 循环、Function Calling、流式输出、记忆与压缩、安全门控、MCP 客户端、Skill、Hook、子 Agent、多 Agent 团队、git worktree 隔离</td></tr>
</table>

---

## 2. 解决方案分层（Clean Architecture）

领域核心不依赖任何 UI；界面层只负责把核心产出的「事件流」渲染成终端画面。这使同一套领域逻辑既能被任意前端复用，也能被单元测试独立验证。

<table>
<tr><th align="left">项目</th><th align="left">职责</th><th align="left">依赖方向</th></tr>
<tr>
<td><b>VestiCode.Core</b></td>
<td>领域核心：Agent 循环、Provider 抽象、工具体系、记忆、安全、MCP、Skill、Hook、子 Agent、团队、worktree。<b>不含任何 UI 代码</b>。</td>
<td>仅 .NET BCL + 少量库（YamlDotNet 等）</td>
</tr>
<tr>
<td><b>VestiCode.Cli</b></td>
<td>宿主与界面：Generic Host + 依赖注入 + 分层配置 + Serilog 日志 + 控制台 TUI。消费 Core 的事件流并渲染。</td>
<td>→ VestiCode.Core</td>
</tr>
<tr>
<td><b>VestiCode.UnitTests</b></td>
<td>xUnit 单元测试（58 个），覆盖 Agent 循环、安全、MCP、Skill、Hook、子 Agent、团队、会话、压缩等核心组件。</td>
<td>→ VestiCode.Core</td>
</tr>
</table>

---

## 3. Agent 核心架构（四大组件）

一个 AI Agent 由四个核心组件构成，本项目的落点如下。其中 **Agent Loop 是 Agent 区别于普通 LLM 对话的关键**——它让模型能反复思考、行动、观察，自我驱动直至任务完成。

<table>
<tr><th align="left">组件</th><th align="left">角色</th><th align="left">本项目实现</th></tr>
<tr><td><b>LLM（大脑）</b></td><td>推理与决策</td><td><code>ILlmProvider</code> + <code>OpenAIProvider</code> / <code>AnthropicProvider</code>（SSE 流式）</td></tr>
<tr><td><b>Agent Loop（控制循环）</b></td><td>驱动 ReAct 迭代</td><td><code>AgentLoop.RunAsync</code>（<code>async IAsyncEnumerable&lt;AgentEvent&gt;</code>）</td></tr>
<tr><td><b>Memory（记忆）</b></td><td>维持上下文</td><td><code>ConversationHistory</code> + <code>JsonlSessionStore</code> + <code>ContextCompressor</code> + <code>AutoNoteManager</code></td></tr>
<tr><td><b>Tools（工具）</b></td><td>与外界交互</td><td><code>ITool</code> + <code>ToolRegistry</code> + <code>ToolExecutor</code>（7+ 内置 + 动态 MCP）</td></tr>
</table>

### 3.1 总体架构图

<b>图例</b>：🟥 核心循环 · 🟦 宿主 / 普通组件 · 🟩 只读 · 🟧 写入 · 虚线框 = 可选扩展

<table width="100%">
<tr><td align="center" style="border:2px solid #4a6da7;border-radius:10px;padding:12px;background:#eaf1fb">
<b>① VestiCode.Cli — 宿主 / 界面层</b>
<table width="100%"><tr>
<td align="center" style="border:1px solid #7a93b8;border-radius:8px;padding:10px;background:#ffffff;font-size:13px"><b>Program.cs</b><br/><span style="color:#555">Generic Host · DI 装配<br/>分层配置 · Serilog</span></td>
<td align="center" style="border:1px solid #7a93b8;border-radius:8px;padding:10px;background:#ffffff;font-size:13px"><b>ConsoleApp</b><br/><span style="color:#555">TUI 主循环</span></td>
<td align="center" style="border:1px solid #7a93b8;border-radius:8px;padding:10px;background:#ffffff;font-size:13px"><b>LineEditor</b><br/><span style="color:#555">输入 / 命令补全</span></td>
<td align="center" style="border:1px solid #7a93b8;border-radius:8px;padding:10px;background:#ffffff;font-size:13px"><b>MarkdownRenderer<br/>StatusIndicator</b><br/><span style="color:#555">渲染 / 状态行</span></td>
</tr></table>
</td></tr>

<tr><td align="center" style="padding:12px 0;font-size:14px;color:#222">
<b>⬇ 用户输入</b>（一条自然语言消息）&nbsp;&nbsp;&nbsp;│&nbsp;&nbsp;&nbsp;<b>⬆ AgentEvent 事件流</b>（逐字文本 / 工具调用 / 结果 / HITL / 完成 —— 边产边渲染）
</td></tr>

<tr><td align="center" style="border:2px solid #4f6228;border-radius:10px;padding:14px;background:#f3f7ee">
<b>② VestiCode.Core — 领域核心层（不含任何 UI 代码）</b>
<br/><br/>
<table align="center"><tr>
<td align="center" style="border:1px solid #7a93b8;border-radius:8px;padding:10px;background:#eef4fb;font-size:13px"><b>ILlmProvider</b><br/><span style="color:#555">OpenAI / Anthropic<br/>SSE 流式</span></td>
<td align="center" style="font-size:12px;color:#444;padding:0 8px">◀ 发送 messages+tools ─<br/>─ 流式回传 token / 工具调用 ▶</td>
<td align="center" style="border:2.5px solid #c0504d;border-radius:10px;padding:16px;background:#fdeceb"><b>AgentLoop</b><br/>ReAct 推理循环<br/><span style="color:#a33">（心脏）</span></td>
<td align="center" style="font-size:12px;color:#444;padding:0 8px">◀ 读历史拼上下文 ─<br/>─ 回填工具结果 ▶</td>
<td align="center" style="border:1px solid #7a93b8;border-radius:8px;padding:10px;background:#eef4fb;font-size:13px"><b>ConversationHistory</b><br/><span style="color:#555">+ Compressor<br/>+ SessionStore + Notes</span></td>
</tr></table>
<div style="font-size:14px;color:#222;padding:10px 0">⬇ 调用工具（每个调用先经 <b>SecurityGuard</b> 门控）</div>
<table align="center"><tr>
<td align="center" style="border:1px solid #7a93b8;border-radius:8px;padding:10px;background:#eef4fb;font-size:13px"><b>ToolRegistry<br/>+ ToolExecutor</b><br/><span style="color:#555">注册 / 查找<br/>超时·异常包裹</span></td>
<td align="center" style="border:1px solid #4f6228;border-radius:8px;padding:10px;background:#eef3e0;font-size:13px"><b>内置工具</b><br/><span style="color:#555">🟩 read / glob / grep / web_fetch<br/>🟧 write / edit / run_command</span></td>
<td align="center" style="border:1px solid #c0504d;border-radius:8px;padding:10px;background:#fdeceb;font-size:13px"><b>SecurityGuard</b><br/><span style="color:#555">黑名单 / 沙箱<br/>三档 / HITL</span></td>
<td align="center" style="border:1px solid #7a93b8;border-radius:8px;padding:10px;background:#eef4fb;font-size:13px"><b>ProviderCatalog</b><br/><span style="color:#555">providers.json<br/>推导协议 / 基址</span></td>
</tr></table>
<div style="border:1.5px dashed #4f6228;border-radius:8px;padding:10px;margin-top:12px;background:#fafdf5;font-size:13px"><b>扩展能力（可选）</b>　MCP 客户端 · Skill · Hook · 子 Agent · 多 Agent 团队（worktree 隔离）· Worktree · 调度模式</div>
</td></tr>
</table>

> **数据流**：界面层把用户输入交给 `AgentLoop`；`AgentLoop` 向左调用 LLM、向右读写历史、向下经安全门控执行工具，并**持续向上产出事件**供界面实时渲染。核心层从不直接操作终端。

---

## 4. 推理流程图（ReAct 主循环）

`AgentLoop.RunAsync` 是系统心脏。一次用户提问会触发一轮或多轮「思考-行动-观察」，直到模型不再请求工具或达到最大轮次。

<table width="100%">
<tr><td align="center" style="border:1px solid #7a93b8;border-radius:8px;padding:10px;background:#eef3f8;font-size:13px"><b>用户消息</b>加入 <code>ConversationHistory</code></td></tr>
<tr><td align="center" style="font-size:18px;color:#5b7aa5">⬇</td></tr>

<tr><td align="center" style="border:2px dashed #4a6da7;border-radius:10px;padding:14px;background:#f7faff">
<b>🔁 轮次循环　round = 1 … MaxRounds</b>
<br/><br/>
<table width="100%">
<tr><td align="center" style="border:1px solid #7a93b8;border-radius:8px;padding:10px;background:#ffffff;font-size:13px"><b>① 准备</b>　检查取消 → 上下文压缩检查（70% 警告 / 90% 压缩）<br/>→ 组装消息（System Prompt + Skill SOP + 历史 + 每轮注入）→ 构造工具定义</td></tr>
<tr><td align="center" style="font-size:18px;color:#5b7aa5">⬇</td></tr>
<tr><td align="center" style="border:1px solid #7a93b8;border-radius:8px;padding:10px;background:#ffffff;font-size:13px"><b>② 调用 LLM</b>　<code>ChatStreamAsync</code>（SSE 流式）<br/>逐块产出并实时上抛事件：<b>TextDelta</b>（文本）· <b>ThinkingDelta</b>（思考）· <b>ToolCallReady</b>（工具调用）· <b>UsageReport</b> · <b>StreamError</b></td></tr>
<tr><td align="center" style="font-size:13px;color:#444">⬇　<b>本轮是否请求了工具？</b></td></tr>
<tr><td align="center">
<table align="center"><tr>
<td align="center" style="border:1.5px solid #c0504d;border-radius:8px;padding:10px;background:#fdeceb;font-size:13px"><b>否（无工具调用）</b><br/>写入最终答复<br/>产出 <code>Done(NoToolCall)</code><br/><b>→ 结束 ✅</b></td>
<td align="center" style="padding:0 16px;font-size:13px;color:#444">◀ 否　│　是 ▶</td>
<td align="center" style="border:1.5px solid #4f6228;border-radius:8px;padding:10px;background:#eef3e0;font-size:13px"><b>是（有工具调用）</b><br/>↓ 进入执行</td>
</tr></table>
</td></tr>
<tr><td align="center" style="font-size:18px;color:#5b7aa5">⬇　（是）</td></tr>
<tr><td align="center" style="border:1px solid #7a93b8;border-radius:8px;padding:10px;background:#ffffff;font-size:13px"><b>③ 记账</b>　把 assistant 的 tool_use 写入历史（必须先于 tool 结果，保证配对）</td></tr>
<tr><td align="center" style="font-size:18px;color:#5b7aa5">⬇</td></tr>
<tr><td align="center" style="border:1px solid #7a93b8;border-radius:8px;padding:12px;background:#ffffff;font-size:13px">
<b>④ 按类别分批执行</b>
<table width="100%"><tr>
<td align="center" style="border:1.5px solid #4f6228;border-radius:8px;padding:10px;background:#eef3e0;font-size:13px"><b>🟩 读类</b><br/>逐个安全门控<br/>→ <b>并发</b>执行 <code>Task.WhenAll</code></td>
<td align="center" style="border:1.5px solid #c0504d;border-radius:8px;padding:10px;background:#fdeceb;font-size:13px"><b>🟧 写类</b><br/>plan-only 拦截 → 安全门控（可弹 HITL）→ Hook 拦截<br/>→ <b>串行</b>执行</td>
</tr></table>
<span style="color:#555">⑤ 每个工具结果（Observation）回填历史，供下一轮"看到"</span>
</td></tr>
<tr><td align="center" style="font-size:14px;color:#5b7aa5">↺ <b>回到轮次顶部</b>（带着新的工具结果继续推理）</td></tr>
</table>
</td></tr>

<tr><td align="center" style="font-size:18px;color:#5b7aa5">⬇　（跑满 MaxRounds 仍未完成）</td></tr>
<tr><td align="center" style="border:1px solid #7a93b8;border-radius:8px;padding:10px;background:#eef3f8;font-size:13px">产出 <code>Done(MaxRounds)</code> <b>→ 结束</b></td></tr>
</table>

**流程层面的关键设计：**

<table>
<tr><th align="left">设计</th><th align="left">说明</th></tr>
<tr><td>流式即可观测</td><td>每个 token / 思考 / 工具调用 / 工具结果都实时以事件产出，推理过程对用户完全透明</td></tr>
<tr><td>读并发 / 写串行</td><td>只读工具无副作用 → <code>Task.WhenAll</code> 并发加速；写类有副作用 → 串行，避免文件竞态</td></tr>
<tr><td>Observation 必回填</td><td>每个工具结果作为 <code>tool</code> 消息写回历史，模型下一轮才能"看到"结果继续推理（ReAct 闭环）</td></tr>
<tr><td>双重终止条件</td><td>模型不再请求工具（<code>NoToolCall</code>）或达到 <code>MaxRounds</code></td></tr>
<tr><td>消息配对约束</td><td>必须先写 assistant 的 tool_use，再写对应 tool 结果，否则协议不配对会被 API 拒绝</td></tr>
</table>

---

## 5. 工具设计（Tool Calling）

### 5.1 工具抽象与契约

所有工具实现统一接口 `ITool`；`ToolRegistry` 负责注册与查找，并把工具元数据导出为 LLM 可理解的 Function Calling 定义；`ToolExecutor` 统一包裹执行（超时、异常转 `ToolResult.Fail`、日志）。

<table align="center">
<tr>
<td align="center" style="border:1px solid #7a93b8;border-radius:8px;padding:12px;background:#eef4fb;font-size:13px"><b>ITool</b>（每个工具）<br/><span style="color:#555">Name · Description<br/>Parameters · Category<br/>ExecuteAsync()</span></td>
<td align="center" style="font-size:13px;color:#444;padding:0 10px"><b>注册</b><br/>➡</td>
<td align="center" style="border:1px solid #7a93b8;border-radius:8px;padding:12px;background:#eef4fb;font-size:13px"><b>ToolRegistry</b><br/><span style="color:#555">查找 + 导出<br/><code>ToolDefinition[]</code></span></td>
<td align="center" style="font-size:13px;color:#444;padding:0 10px"><b>随请求发给</b><br/>➡</td>
<td align="center" style="border:1px solid #7a93b8;border-radius:8px;padding:12px;background:#eef4fb;font-size:13px"><b>LLM</b><br/><span style="color:#555">Function Calling<br/>JSON Schema</span></td>
</tr>
<tr>
<td colspan="5" align="center" style="padding-top:10px;font-size:13px;color:#333"><b>执行时</b>：<code>ToolExecutor.ExecuteAsync</code> 统一包裹 超时 / 异常 / 日志 → 返回 <code>ToolResult</code>（Success + Output / Error）</td>
</tr>
</table>

> `ITool.Category` 默认值为 **Write（保守默认）**，只读工具显式覆盖为 `Read`。这一默认值保证"未声明类别的工具一律按写入处理"，安全优先。

### 5.2 内置工具清单

<table>
<tr><th align="left">工具</th><th align="left">类别</th><th align="left">作用</th><th align="left">并发性</th></tr>
<tr><td><code>read_file</code></td><td>Read</td><td>读取文件内容（去除 UTF-8 BOM）</td><td>可并发</td></tr>
<tr><td><code>glob</code></td><td>Read</td><td>通配符匹配文件路径</td><td>可并发</td></tr>
<tr><td><code>grep</code></td><td>Read</td><td>正则搜索文件内容，返回 文件:行号:内容</td><td>可并发</td></tr>
<tr><td><code>web_fetch</code></td><td>Read</td><td>抓取 URL 内容</td><td>可并发</td></tr>
<tr><td><code>write_file</code></td><td>Write</td><td>新建文件（已存在则拒绝，提示改用 edit）</td><td>串行</td></tr>
<tr><td><code>edit_file</code></td><td>Write</td><td>精确字符串替换（old_string 须在文件中唯一）</td><td>串行</td></tr>
<tr><td><code>run_command</code></td><td>Write</td><td>持久 shell 执行命令（cd / 环境变量跨调用保持）</td><td>串行</td></tr>
<tr><td><code>sub_agent</code></td><td>Write</td><td>派生子 Agent 处理子任务（run-to-end）</td><td>串行</td></tr>
<tr><td><code>{server}__{tool}</code></td><td>Read/Write</td><td>MCP 动态工具（运行期从外部 server 发现注册）</td><td>按类别</td></tr>
</table>

> **Category 的双重作用**：① 决定主循环"读并发 / 写串行"的分批；② 决定安全门控的默认放行策略（读放行、写询问）。

### 5.3 工具调用全链路

<table align="center">
<tr>
<td align="center" style="border:1px solid #7a93b8;border-radius:8px;padding:10px;background:#ffffff;font-size:13px"><b>① LLM 产出</b><br/><code>ToolCall(name,args)</code></td>
<td align="center" style="color:#5b7aa5">➡</td>
<td align="center" style="border:1px solid #7a93b8;border-radius:8px;padding:10px;background:#ffffff;font-size:13px"><b>② ToolRegistry</b><br/><span style="color:#555">.Get(name)</span></td>
<td align="center" style="color:#5b7aa5">➡</td>
<td align="center" style="border:1.5px solid #c0504d;border-radius:8px;padding:10px;background:#fdeceb;font-size:13px"><b>③ SecurityGuard</b><br/><span style="color:#555">Allow / Ask / Deny</span></td>
<td align="center" style="color:#5b7aa5">➡</td>
<td align="center" style="border:1px solid #7a93b8;border-radius:8px;padding:10px;background:#ffffff;font-size:13px"><b>④ Hook</b><br/><span style="color:#555">TOOL_PRE_EXEC</span></td>
</tr>
<tr><td colspan="7" align="center" style="padding:8px;font-size:13px;color:#444">⬇　分支：<b>Ask</b> → 弹 HITL（A/S/P/D）；&nbsp; <b>Deny</b> / Hook 拦截 → 失败原因回灌模型，跳过执行</td></tr>
<tr>
<td align="center" style="border:1px solid #7a93b8;border-radius:8px;padding:10px;background:#ffffff;font-size:13px"><b>⑤ ToolExecutor</b><br/><span style="color:#555">超时 / 异常包裹</span></td>
<td align="center" style="color:#5b7aa5">➡</td>
<td align="center" style="border:1px solid #7a93b8;border-radius:8px;padding:10px;background:#ffffff;font-size:13px"><b>⑥ ToolResult</b></td>
<td align="center" style="color:#5b7aa5">➡</td>
<td align="center" style="border:1px solid #7a93b8;border-radius:8px;padding:10px;background:#ffffff;font-size:13px"><b>⑦ 回填历史</b>（tool 消息）<br/>+ 产出 <code>ToolResultEvent</code></td>
<td align="center" style="color:#5b7aa5">➡</td>
<td align="center" style="border:1px solid #7a93b8;border-radius:8px;padding:10px;background:#ffffff;font-size:13px"><b>⑧ Hook</b><br/><span style="color:#555">TOOL_POST_EXEC</span></td>
</tr>
</table>

---

## 6. 记忆架构（Memory）

VestiCode 实现了**三层记忆 + 两段式压缩 + 四类长期笔记**。

<table>
<tr><th align="left">层级</th><th align="left">实现</th><th align="left">说明</th></tr>
<tr><td>短期记忆</td><td><code>ConversationHistory</code></td><td>当前会话完整消息列表（System / User / Assistant / Tool 四种角色）</td></tr>
<tr><td>工作记忆</td><td>工具结果回填 + 每轮注入</td><td>中间状态（工具 Observation、plan-only 提醒等）随历史滚动</td></tr>
<tr><td>持久记忆</td><td><code>JsonlSessionStore</code></td><td>会话落盘 <code>{id}.jsonl</code> + <code>{id}.meta.json</code>；支持 <code>--resume</code> 恢复、跳过损坏行、未配对 tool_use 处截断、&gt;30 分钟注入时间提醒</td></tr>
<tr><td>长期偏好</td><td><code>AutoNoteManager</code></td><td>每 5 轮按四类（用户偏好 / 纠正反馈 / 项目知识 / 参考资料）增量提炼笔记，跨会话可用</td></tr>
</table>

### 6.1 两段式上下文压缩

<table width="100%">
<tr><td align="center" style="border:1.5px solid #4a6da7;border-radius:8px;padding:10px;background:#eef4fb;font-size:13px"><b>层 1（每次请求前，廉价、无 LLM）</b><br/><code>ToolResultTruncator</code> 截断超大工具结果并落盘留预览</td></tr>
<tr><td align="center" style="font-size:18px;color:#5b7aa5">⬇</td></tr>
<tr><td align="center" style="border:1.5px solid #4a6da7;border-radius:8px;padding:12px;background:#f7faff;font-size:13px">
<b>层 2　按"估算 token / 上下文窗口"比例分三档</b>
<table width="100%"><tr>
<td align="center" style="border:1.5px solid #4f6228;border-radius:8px;padding:12px;background:#eef3e0;font-size:13px"><b>&lt; 70%</b><br/><span style="color:#555">正常，不处理</span></td>
<td align="center" style="border:1.5px solid #c79c2e;border-radius:8px;padding:12px;background:#fbf4e0;font-size:13px"><b>≥ 70%　仅警告</b><br/><span style="color:#555">产出 <code>ContextWarningEvent</code><br/>提示用户，<b>不改动历史</b></span></td>
<td align="center" style="border:1.5px solid #c0504d;border-radius:8px;padding:12px;background:#fdeceb;font-size:13px"><b>≥ 90%　自动压缩</b><br/><span style="color:#555"><code>StructuredSummarizer</code> 调 LLM<br/>生成结构化摘要替换早期消息<br/><b>保留最近 4 条原文</b></span></td>
</tr></table>
</td></tr>
<tr><td align="center" style="font-size:14px;color:#5b7aa5">⬇　（摘要连续失败 2 次）</td></tr>
<tr><td align="center" style="border:1px solid #7a93b8;border-radius:8px;padding:10px;background:#eef3f8;font-size:13px"><code>CircuitBreaker</code> <b>熔断</b>，停止自动压缩，防止反复消耗 token；&nbsp;<code>/compress</code> 手动触发会复位熔断并重试</td></tr>
</table>

---

## 7. 安全架构（边界控制）

`SecurityGuard` 在每个工具执行前进行多层纵深防御。

<table width="100%">
<tr><td align="center" style="border:1px solid #7a93b8;border-radius:8px;padding:8px;background:#ffffff;font-size:13px"><b>工具调用</b>（带 name / path / command）</td></tr>
<tr><td align="center" style="font-size:18px;color:#5b7aa5">⬇</td></tr>
<tr><td align="center" style="border:2px solid #c0504d;border-radius:10px;padding:12px;background:#fdeceb">
<b>SecurityGuard　按顺序逐层判定</b>
<table width="100%">
<tr><td style="border:1px solid #c0504d;border-radius:8px;padding:10px;background:#ffffff;font-size:13px"><b>① CommandBlacklist（绝对红线，所有档位生效，规则不可覆盖）</b><br/><span style="color:#555"><code>rm -rf /</code> / fork bomb / <code>curl|sh</code> 等 → <b>Deny</b>；复合命令 <code>ls &amp;&amp; rm…</code> 逐段检查</span></td></tr>
<tr><td align="center" style="font-size:13px;color:#5b7aa5">⬇ 未命中</td></tr>
<tr><td style="border:1px solid #c0504d;border-radius:8px;padding:10px;background:#ffffff;font-size:13px"><b>② PathSandbox</b><br/><span style="color:#555">路径越出项目根（<code>../outside</code>）→ <b>Deny</b></span></td></tr>
<tr><td align="center" style="font-size:13px;color:#5b7aa5">⬇ 未命中</td></tr>
<tr><td style="border:1px solid #c0504d;border-radius:8px;padding:10px;background:#ffffff;font-size:13px"><b>③ 规则 + 档位策略</b>（会话 &gt; 项目 &gt; 全局 &gt; 档位默认）<br/><span style="color:#555">strict：写 / 敏感全部 Ask　·　normal（默认）：读放行 / 写 Ask　·　permissive：仅靠①②</span></td></tr>
</table>
</td></tr>
<tr><td align="center" style="font-size:18px;color:#5b7aa5">⬇　判定结果</td></tr>
<tr><td align="center">
<table align="center"><tr>
<td align="center" style="border:1.5px solid #4f6228;border-radius:8px;padding:12px;background:#eef3e0;font-size:13px"><b>Allow</b><br/><span style="color:#555">直接执行</span></td>
<td align="center" style="border:1.5px solid #c79c2e;border-radius:8px;padding:12px;background:#fbf4e0;font-size:13px"><b>Ask → HITL 弹窗</b><br/><span style="color:#555">A 本次 · S 本会话 · P 永久 · D 拒绝<br/>（P → 写入 <code>./.vesticode/security.json</code>）</span></td>
<td align="center" style="border:1.5px solid #c0504d;border-radius:8px;padding:12px;background:#fdeceb;font-size:13px"><b>Deny</b><br/><span style="color:#555">拦截，原因回灌模型促其调整</span></td>
</tr></table>
</td></tr>
</table>

---

## 8. Provider 抽象与流式

用户只需配置 `Name / Model / ApiKey`；协议与基址由内置的 `providers.json` 推导，新增后端只需追加一行配置、无需改代码。

<table align="center">
<tr>
<td align="center" style="border:1px solid #7a93b8;border-radius:8px;padding:10px;background:#ffffff;font-size:13px"><b>appsettings.json</b><br/><span style="color:#555">Name / Model / ApiKey</span></td>
<td align="center" style="color:#5b7aa5">➡</td>
<td align="center" style="border:1px solid #7a93b8;border-radius:8px;padding:10px;background:#ffffff;font-size:13px"><b>ProviderCatalog</b><br/><span style="color:#555">嵌入 <code>providers.json</code><br/>Name → Protocol + BaseUrl</span></td>
<td align="center" style="color:#5b7aa5">➡</td>
<td align="center" style="border:1px solid #7a93b8;border-radius:8px;padding:10px;background:#ffffff;font-size:13px"><b>LlmProviderFactory</b><br/><span style="color:#555">按 Protocol 实例化</span></td>
</tr>
<tr><td colspan="5" align="center" style="font-size:18px;color:#5b7aa5;padding:6px">⬇　按 Protocol 二选一</td></tr>
<tr>
<td colspan="2" align="center" style="border:1px solid #7a93b8;border-radius:8px;padding:10px;background:#eef4fb;font-size:13px"><b>OpenAIProvider</b><br/><span style="color:#555">openai 协议（DeepSeek 复用）</span></td>
<td align="center" style="color:#444">或</td>
<td colspan="2" align="center" style="border:1px solid #7a93b8;border-radius:8px;padding:10px;background:#eef4fb;font-size:13px"><b>AnthropicProvider</b><br/><span style="color:#555">anthropic 协议</span></td>
</tr>
<tr><td colspan="5" align="center" style="font-size:13px;color:#5b7aa5;padding:6px">⬇ <code>ChatStreamAsync(messages, tools)</code> : <code>IAsyncEnumerable&lt;LlmStreamItem&gt;</code></td></tr>
<tr><td colspan="5" align="center" style="border:1.5px dashed #4a6da7;border-radius:8px;padding:10px;background:#f7faff;font-size:13px"><b>SSE 逐块解析</b> → <b>TextDelta</b>（文本）· <b>ThinkingDelta</b>（思考）· <b>ToolCallReady</b>（工具调用）· <b>UsageReport</b>（用量）· <b>StreamError</b>（错误）</td></tr>
</table>

<table>
<tr><th align="left">Name</th><th align="left">Protocol</th><th align="left">BaseUrl</th></tr>
<tr><td>openai</td><td>openai</td><td>https://api.openai.com/v1</td></tr>
<tr><td>anthropic</td><td>anthropic</td><td>https://api.anthropic.com</td></tr>
<tr><td>deepseek</td><td>openai</td><td>https://api.deepseek.com</td></tr>
</table>

---

## 9. 并发模型

VestiCode 的并发集中在 Agent 循环与工具执行，采用「读并发 / 写串行」并辅以多种异步原语。

<table>
<tr><th align="left">机制</th><th align="left">用途</th><th align="left">原语</th></tr>
<tr><td>异步流</td><td>LLM SSE 与 Agent 事件边产边消费</td><td><code>IAsyncEnumerable&lt;T&gt;</code> + <code>await foreach</code></td></tr>
<tr><td>读类并发</td><td>同一轮多个只读工具并行</td><td><code>Task.WhenAll</code></td></tr>
<tr><td>写类串行</td><td>避免写工具相互竞态</td><td>顺序 <code>foreach</code> + <code>await</code></td></tr>
<tr><td>Shell 串行</td><td>持久 shell 命令排队</td><td><code>SemaphoreSlim</code></td></tr>
<tr><td>人在回路</td><td>挂起等待用户授权而不阻塞线程</td><td><code>TaskCompletionSource</code>（RunContinuationsAsynchronously）</td></tr>
<tr><td>取消</td><td>轮次边界优雅停止 / 流中途中断</td><td><code>CancellationToken</code> + <code>[EnumeratorCancellation]</code></td></tr>
<tr><td>团队并行</td><td>多成员各自 worktree 物理隔离并行</td><td>git worktree + 进程级文件隔离</td></tr>
</table>

---

## 10. 高级模块

<table>
<tr><th align="left">模块</th><th align="left">能力</th><th align="left">关键类型</th></tr>
<tr><td>MCP（客户端）</td><td>stdio / HTTP 连接外部 MCP server，动态注册工具/资源/提示词（首次调用惰性发现并缓存）；工具命名 <code>{server}__{tool}</code></td><td><code>McpManager</code> / <code>McpClient</code> / <code>McpAdapters</code></td></tr>
<tr><td>Skill</td><td>两阶段技能：阶段 1 描述注入 → 阶段 2 SOP 钉入；可声明工具白名单约束 Agent 可见工具</td><td><code>SkillRegistry</code> / <code>SkillTool</code></td></tr>
<tr><td>Hook</td><td>生命周期事件钩子（轮次、工具 pre/post），可拦截或记录；YAML 配置，加载期校验</td><td><code>HookEngine</code> / <code>HookLoader</code></td></tr>
<tr><td>子 Agent</td><td>run-to-end 子任务执行；角色化工具过滤（如 explorer 仅 read/glob/grep）；全局禁止递归创建</td><td><code>SubAgentRunner</code> / <code>RoleLoader</code> / <code>ToolFilter</code></td></tr>
<tr><td>多 Agent 团队</td><td>Lead 用 LLM 拆解目标 → 成员各自 git worktree 隔离工作 → 合并回 main（冲突 LLM 逐文件裁决，失败回滚）</td><td><code>LeadAgent</code> / <code>TeamMember</code> / <code>GitMerger</code></td></tr>
<tr><td>Worktree</td><td>git worktree 隔离工作区：create / enter / merge / exit(非破坏性离开) / remove</td><td><code>GitWorktreeManager</code></td></tr>
<tr><td>调度模式</td><td>双锁激活后剥夺 Lead 的读写/执行工具，注入 10 阶段指挥官工作流，迫使其只委派</td><td><code>DispatchScheduler</code></td></tr>
</table>

### 10.1 多 Agent 团队流程

<table width="100%">
<tr><td align="center" style="border:1px solid #7a93b8;border-radius:8px;padding:10px;background:#eef3f8;font-size:13px"><code>/team run &lt;team&gt; &lt;目标&gt;</code>（须在 git 仓库中）</td></tr>
<tr><td align="center" style="font-size:18px;color:#5b7aa5">⬇</td></tr>
<tr><td align="center" style="border:1.5px solid #4a6da7;border-radius:8px;padding:12px;background:#eef4fb;font-size:13px"><b>① LeadAgent.Decompose（LLM 拆解）</b><br/><span style="color:#555">输入：目标 × 成员角色 × 项目文件快照　→　输出：带 <code>assignee / files / dependsOn</code> 的子任务计划（按文件边界解耦，避免冲突）</span></td></tr>
<tr><td align="center" style="font-size:18px;color:#5b7aa5">⬇</td></tr>
<tr><td align="center" style="border:1px solid #7a93b8;border-radius:8px;padding:12px;background:#ffffff;font-size:13px">
<b>② DispatchLoop</b>　按计划分配 + 依赖顺序，路由到成员；成员在<b>各自 worktree 并行</b>工作
<table width="100%"><tr>
<td align="center" style="border:1.5px solid #4f6228;border-radius:8px;padding:10px;background:#eef3e0;font-size:13px"><b>成员 A</b><br/><span style="color:#555">worktree <code>vesticode/A</code><br/>ReAct 完成 → git commit</span></td>
<td align="center" style="border:1.5px solid #4f6228;border-radius:8px;padding:10px;background:#eef3e0;font-size:13px"><b>成员 B</b><br/><span style="color:#555">worktree <code>vesticode/B</code><br/>ReAct 完成 → git commit</span></td>
</tr></table>
</td></tr>
<tr><td align="center" style="font-size:18px;color:#5b7aa5">⬇</td></tr>
<tr><td align="center" style="border:1.5px solid #c0504d;border-radius:8px;padding:12px;background:#fdeceb;font-size:13px"><b>③ GitMerger</b>　逐分支 merge 回 main<br/><span style="color:#555">无冲突直接合并　·　有冲突 → LLM 逐文件裁决　·　裁决失败 → 回滚并上报</span></td></tr>
<tr><td align="center" style="font-size:18px;color:#5b7aa5">⬇</td></tr>
<tr><td align="center" style="border:1.5px solid #4a6da7;border-radius:8px;padding:12px;background:#eef4fb;font-size:13px"><b>④ LeadAgent.Synthesize（LLM 综合）</b>　汇总各成员产出，给出最终报告</td></tr>
</table>

---

## 11. .NET 技术深度

<table>
<tr><th align="left">.NET 特性</th><th align="left">用法</th></tr>
<tr><td>依赖注入（DI）</td><td><code>Microsoft.Extensions.DependencyInjection</code>：所有组件构造注入；可选依赖用可空构造参数（如 <code>AgentLoop</code> 的 securityGuard / compressor / promptBuilder …）</td></tr>
<tr><td>Generic Host</td><td><code>Host.CreateApplicationBuilder</code> 统一服务装配与生命周期</td></tr>
<tr><td>配置系统</td><td>分层叠加：全局 / 项目 JSON → <code>VESTICODE_CONFIG</code> 指定文件 → 环境变量 → UserSecrets（密钥不入库）</td></tr>
<tr><td>日志</td><td>Serilog 滚动文件日志（<code>~/.vesticode/logs/</code>）+ 构建期自举 logger</td></tr>
<tr><td>异步流</td><td><code>async IAsyncEnumerable&lt;T&gt;</code> + <code>await foreach</code> 贯穿 LLM 流式与 Agent 事件流</td></tr>
<tr><td>并发原语</td><td><code>Task.WhenAll</code> / <code>SemaphoreSlim</code> / <code>TaskCompletionSource</code> / <code>CancellationToken</code></td></tr>
<tr><td>记录类型</td><td><code>record</code> 建模不可变事件/消息（<code>AgentEvent</code> / <code>ChatMessage</code> / <code>ToolResult</code>）</td></tr>
<tr><td>可空引用类型</td><td>全程 <code>Nullable</code> 开启，<code>TreatWarningsAsErrors</code> 强约束</td></tr>
</table>

---

## 12. 事件驱动设计（UI 解耦）

`AgentLoop` 不直接操作终端，而是产出 `AgentEvent`，由 `ConsoleApp` 翻译为画面。同一套领域逻辑因此可换任意 UI。

<table>
<tr><th align="left">事件</th><th align="left">含义</th><th align="left">TUI 呈现</th></tr>
<tr><td><code>RoundStartEvent</code></td><td>新一轮开始</td><td>内部计数</td></tr>
<tr><td><code>TextDeltaEvent</code></td><td>回复文本增量</td><td>逐字流式打印 + Markdown 渲染</td></tr>
<tr><td><code>ThinkingEvent</code></td><td>推理/思考增量</td><td>灰色 Thinking / Reasoning</td></tr>
<tr><td><code>ToolCallEvent</code></td><td>请求调用工具</td><td><code>● tool(args)</code></td></tr>
<tr><td><code>ToolResultEvent</code></td><td>工具返回（携带本次 Arguments）</td><td>多行结果预览</td></tr>
<tr><td><code>ToolBlockedEvent</code></td><td>被安全/Hook/plan-only 拦截</td><td>拦截原因提示</td></tr>
<tr><td><code>HitlRequestEvent</code></td><td>需用户授权</td><td>A/S/P/D 弹窗（<code>TaskCompletionSource</code> 回传决定）</td></tr>
<tr><td><code>UsageEvent</code></td><td>token 用量上报</td><td>状态行计数</td></tr>
<tr><td><code>ContextWarningEvent</code> / <code>CompactionEvent</code></td><td>上下文 70% 警告 / 90% 压缩</td><td>灰色提示行</td></tr>
<tr><td><code>AgentDoneEvent</code></td><td>循环结束（NoToolCall / MaxRounds / Cancelled）</td><td>收尾</td></tr>
<tr><td><code>ErrorEvent</code></td><td>LLM 流不可恢复错误</td><td>红色错误，不崩溃</td></tr>
</table>

---

## 13. 目录结构

<table>
<tr><th align="left">路径</th><th align="left">职责</th></tr>
<tr><td><code>src/VestiCode.Core/Agents/</code></td><td><b>AgentLoop（推理循环·心脏）</b>、AgentEvent</td></tr>
<tr><td><code>src/VestiCode.Core/Llm/</code></td><td>ILlmProvider、OpenAI/Anthropic Provider、ProviderCatalog、ChatMessage</td></tr>
<tr><td><code>src/VestiCode.Core/Tools/</code></td><td>ITool、ToolRegistry、ToolExecutor、Builtin/*（内置工具）</td></tr>
<tr><td><code>src/VestiCode.Core/Conversation/</code></td><td>ConversationHistory、ContextCompressor、StructuredSummarizer、CircuitBreaker、ToolResultTruncator</td></tr>
<tr><td><code>src/VestiCode.Core/Memory/</code></td><td>JsonlSessionStore（会话持久化）</td></tr>
<tr><td><code>src/VestiCode.Core/Security/</code></td><td>SecurityGuard、CommandBlacklist、PathSandbox、SecurityPolicy</td></tr>
<tr><td><code>src/VestiCode.Core/{Mcp,Skills,Hooks,SubAgents,Teams,Worktree,Notes,Prompts}/</code></td><td>各高级模块</td></tr>
<tr><td><code>src/VestiCode.Cli/Program.cs</code></td><td>宿主 + DI + 分层配置 + 日志装配</td></tr>
<tr><td><code>src/VestiCode.Cli/Tui/</code></td><td>ConsoleApp、LineEditor、MarkdownRenderer、StatusIndicator</td></tr>
<tr><td><code>tests/VestiCode.UnitTests/</code></td><td>58 个 xUnit 测试</td></tr>
</table>
