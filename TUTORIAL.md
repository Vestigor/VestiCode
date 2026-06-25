# VestiCode 使用教程

VestiCode 是用 C# / .NET 10 开发的终端 AI 编程助手（Claude Code 风格）。可作为 `vesticode` 命令在任意项目目录中使用。

## 目录

1. [快速开始](#1-快速开始)
2. [基本对话](#2-基本对话)
3. [Provider 配置](#3-provider-配置)
4. [工具系统](#4-工具系统)
5. [命令系统](#5-命令系统)
6. [对话管理](#6-对话管理)
7. [安全系统](#7-安全系统)
8. [MCP 协议](#8-mcp-协议)
9. [Skill 技能](#9-skill-技能)
10. [Hook 钩子](#10-hook-钩子)
11. [子 Agent](#11-子-agent)
12. [Worktree 工作区隔离](#12-worktree-工作区隔离)
13. [Agent Team](#13-agent-team)
14. [调度模式](#14-调度模式)
15. [项目指令与笔记](#15-项目指令与笔记)
16. [快捷键与启动参数](#16-快捷键与启动参数)

---

## 1. 快速开始

**要求**：.NET SDK 10、Git ≥ 2.30（Worktree 功能）、Node/npx（仅文件系统 MCP server 需要）。

### 开发期（用 SDK 直接跑）

```bash
dotnet build VestiCode.slnx
dotnet run --project src/VestiCode.Cli      # 首次运行会引导配置 API Key
dotnet test VestiCode.slnx                  # 58 单元测试
```

### 安装到本机

`install-local.sh` 自动探测平台、构建自包含单文件、安装到 `~/.local/bin`（macOS 上还会 ad-hoc 重签名）：

```bash
./install-local.sh                 # 自动探测当前平台
./install-local.sh linux-x64       # 也可显式指定 RID
vesticode                          # 之后任意项目目录可用
```

> 若提示 `~/.local/bin` 不在 PATH，按提示把它加入 PATH 后重开终端。原生 Windows 请在 WSL / Git-Bash 中运行该脚本。

### 首次配置

首次运行若当前 Provider 未配置 API Key，会进入**交互式向导**，把配置写入全局 `~/.vesticode/appsettings.json`。也可用环境变量临时提供：

```bash
export VESTICODE_API_KEY=sk-xxx
vesticode
```

---

## 2. 基本对话

界面是**内联式**（文字可原生选中、内嵌 Markdown 渲染、彩色），启动后显示：

```
   /\___/\     VestiCode v0.1.0
  /  o o  \    deepseek (deepseek-chat)
 (    ^    )   /Users/bruce/Desktop/project/VestiCode
  \ \___/ /    9 tools registered
   \/___\/     THINK · CODE · ACT   ·   /help 命令 · esc 中断

❯ 读一下 README.md

● read_file(../README.md)
  ⎿ # VestiCode — 终端 AI 编程助手…

README.md 说明这是一个 .NET 的 AI 编程助手项目…
```

### 操作

| 操作 | 方式 |
|------|------|
| 提交消息 | 输入内容 → **Enter** |
| 中断生成 | **Esc** |
| 退出 | **Ctrl+C**（自动保存会话 + 更新笔记） |
| 补全命令 | 输入 `/hel` → **Tab** |

- AI 回复**逐字流式**输出。
- 底部是**活动区域**：运行时显示一行 `✻ …（esc 中断）` 状态行；思考结束以 `● Thought for Xs` 提交在上方（思考过程本身不展开显示）。
- 工具调用显示为 `● 工具名(参数) (耗时)` + `⎿ 结果预览`（多行）。

> 采用内联界面（而非全屏）是为了支持终端原生文字选中/复制；运行期间会隐藏终端光标，避免在 `●`/`✻` 上闪烁。

---

## 3. Provider 配置

配置用 **JSON**（.NET `IConfiguration`），节名 `VestiCode`，**只承载 LLM 配置，且只识别 `Name`/`Model`/`ApiKey`**——协议与 API 基址由 `Name` 自动推导（内置 `providers.json` 推导表）。

```jsonc
// ~/.vesticode/appsettings.json（全局）或 ./.vesticode/appsettings.json（项目级，优先）
{
  "VestiCode": {
    "ActiveProvider": "deepseek",
    "Providers": [
      { "Name": "openai",    "Model": "gpt-4o",            "ApiKey": "" },
      { "Name": "anthropic", "Model": "claude-sonnet-4-6", "ApiKey": "" },
      { "Name": "deepseek",  "Model": "deepseek-chat",     "ApiKey": "" }
    ]
  }
}
```

**配置分层**（后者覆盖前者）：

1. 全局 `~/.vesticode/appsettings.json`
2. 项目 `./.vesticode/appsettings.json`
3. `VESTICODE_CONFIG=/path/to/config.json` 指定文件（文件源里优先级最高）
4. 环境变量 / UserSecrets（适合放 Key，不入仓）

**要点**：

- `Name` 必须在推导表中（内置 `openai` / `anthropic` / `deepseek`），否则报"不支持的协议/Provider"。
- DeepSeek 与 OpenAI 同协议（`openai`），共用同一 Provider 实现；`deepseek-reasoner` 的推理内容以 `[Reasoning]` 灰色渲染。
- API Key 建议用 `VESTICODE_API_KEY` 环境变量或 UserSecrets，不写进入仓文件。
- 加新后端（如 Kimi/GLM/Ollama）：在 `src/VestiCode.Core/Llm/providers.json` 追加一条 `Name→Protocol/BaseUrl` 即可，无需改代码。

---

## 4. 工具系统

7 个内置工具，Agent 自主选择。**读类并发执行、写类串行执行**。

| 工具 | 功能 | 类型 |
|------|------|------|
| `read_file` | 读文件（输出带行号；UTF-8 / GBK / Latin-1 自动尝试；offset/limit 分段） | 读 |
| `write_file` | 写新文件（已存在则报错，提示改用 edit_file） | 写 |
| `edit_file` | 精确替换（old_string 必须在文件中唯一出现） | 写 |
| `run_command` | 持久 shell 执行命令（cd/env 跨调用保持） | 写 |
| `glob` | 按模式查找文件（如 `**/*.cs`） | 读 |
| `grep` | 正则搜索内容，返回 文件:行号:内容 | 读 |
| `web_fetch` | 抓取网页内容 | 读 |

此外有系统工具：`skill_loader`（激活 Skill）、`sub_agent`（委派子工作器），以及 Team 场景下成员专属的 `team_*`。启动横幅里的「9 tools」= 7 内置 + `sub_agent` + `skill_loader`。

### 工具结果落盘

单个工具结果超过 **100K 字符**时，完整内容写入 `~/.vesticode/tool_results/`，对话只留约 2K 预览；单轮合计过大时从最大的结果开始截断。

---

## 5. 命令系统

`/` 开头触发命令，Tab 可补全；未知命令引导到 `/help`；非 `/` 输入直接发给 AI。`/help <命令>` 查看某命令的完整子命令与用法。

| 命令 | 用途 |
|------|------|
| `/help [命令]` | 列出命令 / 查看详情 |
| `/clear` | 清空对话 + 清空已激活 Skill |
| `/status` | 显示 Provider / Token / 安全档位 / Plan |
| `/mode <strict\|normal\|permissive>` | 切换安全档位 |
| `/plan` | 切换 plan-only 模式（仅只读工具） |
| `/dispatch` | 切换调度（指挥官）模式（双锁，见 §14） |
| `/permission` | 显示当前权限说明 |
| `/compress` | 手动触发上下文压缩（并复位压缩熔断） |
| `/review [路径]` | 注入代码审查提示 |
| `/session <list\|load <id>\|new\|delete <id>>` | 会话管理 |
| `/skill <list\|<name>\|off <name>>` | Skill 管理 |
| `/memory [分类]` | 查看自动笔记 |
| `/commit` | 激活 commit Skill 生成提交信息 |
| `/test` | 激活 test Skill 生成/运行测试 |
| `/tasks <list\|kill <id>>` | 子 Agent 任务：列出 / 终止 |
| `/worktree <status\|list\|create\|enter\|merge\|exit\|remove>` | Git worktree（见 §12） |
| `/team <list\|run <name> <goal>>` | Team 管理（见 §13） |

---

## 6. 对话管理

### 上下文压缩（两层）

- **层 1**：每次请求前截断过大的工具结果（落盘 + 预览）。
- **层 2**：token 估算达上下文窗口 **70% 仅警告**、**90% 才自动压缩**为结构化摘要（保留最近 4 条原文）；压缩后能继续正确回答上文。
- **熔断**：连续 2 次摘要失败自动停止自动压缩；`/compress` 手动触发会复位熔断并重试。

### 会话持久化

每轮后自动保存到 `~/.vesticode/sessions/{id}.jsonl`（追加写 O(1)），并写 `{id}.meta.json`（含 id/title/created_at/last_active_at/message_count/model/provider）。

恢复时自动：跳过损坏行、在未配对的 tool_use 处截断、时间跨度 > 30 分钟注入提醒。

```bash
vesticode --resume     # 启动时恢复最近一次会话
```

---

## 7. 安全系统

三档权限：`/mode <档位>` 切换。

| 档位 | 读类工具 | 写类工具 | 路径限制 |
|------|---------|---------|---------|
| **strict** | 仅白名单路径 | 全部询问 | 白名单 glob（源码/常见配置类型 + 规则声明） |
| **normal**（默认） | 直接放行 | 写入时询问 | 项目目录内 |
| **permissive** | 直接放行 | 直接放行 | 仅禁止 `..` 越界 |

命令黑名单（如 `rm -rf /`，含 `ls && rm` 复合命令逐段检查）在**所有档位**生效；路径沙箱拦截 `../outside` 越界。

### 人在回路（HITL）

无法自动放行时弹出确认：

```
⚠ 安全确认: run_command(command=dotnet add package X)
  当前模式: Normal
  [A]llow once  [S]ession allow  [P]ermanent allow  [D]eny
```

- **A** 本次 / **S** 本会话 / **P** 永久（写入 `./.vesticode/security.json`）/ **D** 拒绝（按 D 后可附一句原因，回车跳过；原因会回灌模型促其调整）。
- 下次同样操作命中已存规则，不再询问。

### 安全规则文件

```jsonc
// ./.vesticode/security.json（项目级）或 ~/.vesticode/security.json（全局）
{
  "Rules": [
    { "Tool": "run_command", "CommandPattern": "dotnet *", "Action": "allow" },
    { "Tool": "write_file",  "PathPattern": "*.env",       "Action": "deny" }
  ]
}
```

优先级：**会话级 > 项目级 > 全局级 > 档位默认**。

---

## 8. MCP 协议

连接外部 MCP Server 扩展工具集。**无内置默认**：不配文件即没有 server。

```yaml
# ./.vesticode/mcp.yaml（项目级）或 ~/.vesticode/mcp.yaml（全局，按 name 并集、项目覆盖同名）
servers:
  - name: fs
    transport: stdio
    command: npx
    args: [-y, "@modelcontextprotocol/server-filesystem", "${cwd}"]
    timeout: 30

  - name: my-remote
    transport: http
    url: http://localhost:8080
    headers:
      Authorization: "Bearer ${env:MY_TOKEN}"
    timeout: 30
```

完整字段见仓库根 [example.mcp.yaml](../config-examples/example.mcp.yaml)。

- **占位符**：`${cwd}` / `${workspaceRoot}` → 当前工作目录；`${home}` → 主目录；`${env:NAME}` → 环境变量。路径类参数为有效目录则用之，否则回退当前工作目录。
- **工具命名**：注册为 `{server}__{tool}`（用 `__` 而非 `/`，符合 LLM 函数名规范），如 `fs__read_file`、`my-remote__search`。
- 启动并行连接所有 server，失败仅记警告、不阻塞。`mcp_resource` / `mcp_prompt` 首次调用时惰性发现资源/模板并缓存。

---

## 9. Skill 技能

Skill 是 YAML frontmatter + Markdown 正文的专业 SOP。

```markdown
---
name: my-skill
description: 我的技能
tools: [read_file, glob, grep]
---

# My Skill SOP
1. 用 glob 了解结构
2. 用 grep 搜关键模式
3. 输出报告
```

### 三级优先级（同名覆盖）

| 优先级 | 路径 |
|--------|------|
| 项目级 | `./.vesticode/skills/*.md` |
| 全局级 | `~/.vesticode/skills/*.md` |
| 内置 | 随程序嵌入（基座，始终可用） |

### 内置 Skill

| Skill | 描述 |
|-------|------|
| `commit` | 生成 Conventional Commits（提交前自动检查 `.gitignore` 是否含 `.vesticode*`） |
| `review` | 全面代码审查（正确性/可读性/错误处理/性能/安全） |
| `test` | 分析变更并生成/运行测试 |

```
/skill list      # 列出可用 Skill
/commit          # 激活并执行（等价于 Agent 调 skill_loader(name="commit")）
```

激活后 Skill 的 SOP **固定注入系统提示**，每轮可见；若 Skill 声明了 `tools` 白名单，Agent 本轮只能看到这些工具 + `skill_loader`。`/clear` 会清空已激活 Skill。

---

## 10. Hook 钩子

事件驱动的自动化规则。**无内置默认**：不配文件即没有 hook。配在 `./.vesticode/hooks.yaml`（项目级）或 `~/.vesticode/hooks.yaml`（全局，两者都生效）。

```yaml
hooks:
  - name: block-rm-rf
    event: tool_pre_exec
    condition:
      match: ALL
      rules:
        - field: tool_name
          operator: exact
          value: run_command
        - field: params.command
          operator: regex
          value: "rm\\s+-rf"
    actions:
      - type: prompt_inject
        text: "拦截: '{{params.command}}' 含危险操作 rm -rf。"
    control: { async: false }   # 拦截事件不允许 async: true
```

完整示例见仓库根 [example.hooks.yaml](../config-examples/example.hooks.yaml)。

### 支持的事件（共 4 种）

| 事件 | 触发时机 | 能否拦截 |
|------|---------|---------|
| `round_start` | 每轮 ReAct 开始 | 否 |
| `message_post_receive` | 收到模型回复后 | 否 |
| `tool_pre_exec` | 工具执行前 | **可拦截**（动作返回拒绝原因即阻止该工具） |
| `tool_post_exec` | 工具执行后 | 否 |

### 操作符 / 动作 / 控制

- **operator**：`exact` / `not` / `regex` / `glob`；`field` 支持点路径（`params.command` 等），`_event` 为事件名。
- **action**：`prompt_inject`（拦截事件用其返回值作拒绝原因）/ `shell` / `http` / `sub_agent`；文本支持 `{{var}}` 模板。
- **control**：`once`（只触发一次）/ `async`（异步）/ `timeout`（秒）。
- **校验**：未知事件名 / 拦截事件 `async: true` → 加载时报错并定位到具体规则；单条规则损坏只跳过它；动作执行失败只记日志、不中断主流程。

---

## 11. 子 Agent

主 Agent 用 `sub_agent` 工具把探索/规划等子任务委派给受约束的子工作器，跑到完成后把结构化报告作为工具结果返回。

```
> 用 explorer 探索项目结构

● sub_agent(task=探索项目结构, role=explorer)
  ⎿ [子 Agent 'explorer' 完成] ## 结果摘要 / ## 关键发现 / ## 文件与代码 / ## 建议
```

不指定 `role` 则为 **fork 模式**：继承当前对话历史 + 复用工具集。

### 内置角色（三级：内置 → 全局 → 项目，同名覆盖）

| 角色 | 工具 | 用途 |
|------|------|------|
| `explorer` | read_file, glob, grep | 只读探索代码结构 |
| `planner` | + run_command | 制定执行计划 |
| `general` | 全部 | 通用综合任务 |

自定义角色放 `~/.vesticode/roles/*.md` 或 `./.vesticode/roles/*.md`（frontmatter：`name`/`description`/`tools_allow`/`max_rounds`/`permission` + 正文 SOP，示例见 `example.role.md`）。子 Agent 不能再调 `sub_agent`（全局禁止，防递归）。

```
/tasks            # 列出子 Agent 任务及状态
/tasks kill <id>  # 终止运行中/排队中的任务（支持 id 前缀）
```

---

## 12. Worktree 工作区隔离

基于 `git worktree` 的物理隔离：同一仓库开多个独立工作目录 + 独立分支（`vesticode/<name>`），互不干扰。

```
/worktree status              # 当前状态
/worktree list                # 列出所有 worktree（● 当前）
/worktree create fix-bug      # 创建（自动复制 .vesticode/ 配置 + 符号链接大依赖目录）
/worktree enter fix-bug       # 切换：进程工作目录切到该 worktree
/worktree merge fix-bug       # 把 vesticode/fix-bug 合并回 main（带 LLM 冲突裁决）
/worktree exit                # 离开当前 worktree，回主仓库（非破坏性，保留 worktree 与分支）
/worktree remove fix-bug      # 删除 worktree 及其分支（有未提交修改需 force）
/worktree remove fix-bug force
```

典型闭环：`create → enter → 改 + commit → merge → remove`。

- 名称含 `..` 等非法字符会被校验拒绝。
- 后台每 5 分钟检查，自动清理**闲置超过 12 小时且无修改**的 worktree（刚创建/在用的不会被动）。
- `vesticode --resume` 可恢复上次会话。

> 在 worktree 里提交走普通 git（让 AI 用 `run_command` 执行 `git add`/`commit`，落到 `vesticode/<name>` 分支）；要并回主干用 `/worktree merge`。`exit` 只是"离开"，不会删除——删除请用 `remove`。

---

## 13. Agent Team

多 Agent 协作小组：Lead 用 LLM 拆解目标、按角色分配给成员、成员各自在 worktree 中并行工作、Lead 合并结果并综合报告。

**定义文件**：

```jsonc
// ~/.vesticode/teams/example.json（全局）或 ./.vesticode/teams/example.json（项目级）
{
  "Name": "example",
  "Description": "示例 Team",
  "LeadRole": "planner",
  "DispatchMode": false,
  "MaxRoundsPerMember": 8,
  "Members": [
    { "Name": "scout",   "Role": "explorer", "Worktree": "scout" },
    { "Name": "builder", "Role": "general",  "Worktree": "builder" }
  ]
}
```

```
/team list                         # 列出 Team 定义
/team run example 实现登录功能      # 运行（需在 git 仓库中）
```

### 执行流程

1. **Lead 拆解**（LLM）：以 `目标 × 成员角色 × 项目文件快照` 为输入，产出带 `assignee`/`files`/`dependsOn` 的子任务计划（按文件边界解耦，避免冲突）。
2. **分派**：按计划与依赖把任务路由到对应成员；每个成员在独立 git worktree（分支 `vesticode/<name>`）中跑完 ReAct，并 `git commit` 到自己分支。
3. **合并**：Lead 逐分支 `git merge` 回 main——无冲突自动合并；有冲突 LLM 逐文件裁决；裁决失败回滚并上报。
4. **综合**（LLM）：汇总各成员产出，给出最终报告。

> 若 LLM 拆解失败，退回"每个成员领同一目标、靠各自角色错开产出"的保底分配。

### 协作工具（成员专属，主 Agent 不可见）

`team_create_task` / `team_list_tasks` / `team_view_task` / `team_update_task` / `team_send_message`（点对点）/ `team_broadcast`（广播）。

---

## 14. 调度模式

调度（指挥官）模式让 Agent **只拆解、只委派，不亲自动手**——激活后失去 `read_file` / `write_file` / `edit_file` / `run_command`，只保留 `sub_agent` / `team_*` 等委派工具，并被注入「理解→拆分→依赖→匹配→委派→监控→收集→验证→仲裁→报告」10 阶段工作流。

采用**双锁**设计，两把锁都开才生效：

| 锁 | 开启方式 |
|----|---------|
| 锁 1（会话内） | 在 vesticode 里输入 `/dispatch` |
| 锁 2（启动） | `vesticode --dispatch` |

只开一把不会激活（`/dispatch` 会提示需配合 `--dispatch`）。此外，Team 定义里 `DispatchMode: true` 会让该团队的 Lead 在拆解时注入同一套 10 阶段工作流。

---

## 15. 项目指令与笔记

### 项目指令

项目根创建 `VESTICODE.md`：

```markdown
# 项目规范
- C# 启用 Nullable，警告即错误
- 测试用 xUnit
@include(docs/ci-rules.md)
```

- `@include` 支持最多 3 层嵌套，拒绝越界路径。
- 启动时读取并作为系统提示注入。
- 全局指令放 `~/.vesticode/instructions.md`（与项目级拼接）。

### 自动笔记

每 5 轮调 LLM **按分类增量**更新（退出时也会做最终更新），四个分类各存一个文件：

| 分类 | 位置 |
|------|------|
| 用户偏好 | `~/.vesticode/notes/user_preferences.md` |
| 纠正反馈 | `~/.vesticode/notes/corrections.md` |
| 项目知识 | `./.vesticode/notes/project_knowledge.md` |
| 参考资料 | `./.vesticode/notes/references.md` |

```
/memory 项目知识     # 查看某分类内容
```

---

## 16. 快捷键与启动参数

### 按键

| 按键 | 功能 |
|------|------|
| **Enter** | 提交消息 / 执行命令 |
| **Esc** | 中断当前生成 |
| **Ctrl+C** | 退出（自动保存 + 笔记更新） |
| **Tab** | 补全 `/` 命令 |
| **A/S/P/D** | HITL 确认（本次 / 本会话 / 永久 / 拒绝） |

模式切换用命令：`/plan`、`/mode <档位>`、`/dispatch`、`/compress`。

### 启动参数与环境变量

```bash
vesticode --resume        # 恢复上次会话
vesticode --dispatch      # 开启调度模式第二把锁（配合会话内 /dispatch）

export VESTICODE_API_KEY=sk-xxx              # 友好提供当前 Provider 的 Key
export VESTICODE_CONFIG=/path/to/config.json # 指定配置文件（优先级最高）
```

---

*相关文档：[README.md](../README.md)（概览）、[ARCHITECTURE.md](ARCHITECTURE.md)（架构）、[REFLECTION.md](REFLECTION.md)（反思报告）。*
