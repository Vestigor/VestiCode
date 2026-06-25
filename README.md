# VestiCode

终端 AI 编程助手（Claude Code 风格），用 **C# / .NET 10** 开发。可作为 `vesticode` CLI 在任意项目目录中开发代码。


## 下载与安装

发布版为**自包含单文件**（无需 .NET）。下载与安装步骤见 **[使用说明.md](使用说明.md)**。

> 从源码构建/开发见下方「快速开始」；打包发布流程见 [scripts/README.md](scripts/README.md)。

## 特性

- **多 Provider**：Anthropic Claude / OpenAI / DeepSeek，SSE 流式逐字输出；DeepSeek reasoner 推理以 `[Reasoning]` 渲染
- **内联 TUI**：原生文本可选中、内嵌 Markdown 渲染、彩色流式输出、`✻` 动态状态行
- **7 个内置工具**：`read_file` `write_file` `edit_file` `run_command` `glob` `grep` `web_fetch`
- **17 条斜杠命令**：`/help` `/clear` `/status` `/mode` `/plan` `/dispatch` `/compress` `/review` `/skill` `/tasks` `/worktree` `/team` 等
- **纵深安全**：命令黑名单 + 路径沙箱 + 三档权限（严格/默认/放行）+ 人在回路（HITL）确认
- **MCP 客户端**：Stdio / HTTP 传输，工具命名 `{server}__{tool}`，配置支持 `${cwd}` 等占位符
- **Skill 系统**：YAML frontmatter + Markdown SOP，内置 + 全局 + 项目三级覆盖，两阶段激活 + 工具白名单
- **Hook 引擎**：4 种生命周期事件（轮次开始 / 消息接收 / 工具前后）+ 条件匹配（exact/not/regex/glob）+ 4 种动作（shell/prompt_inject/http/sub_agent）
- **子 Agent + Team**：角色化子 Agent（explorer/planner/general）、Team 协作（LLM 拆解 + 共享任务清单 + 邮箱 + worktree 隔离与合并裁决）
- **Git Worktree 隔离**：`create / enter / merge / exit / remove` 独立工作区，后台自动清理过期 worktree
- **两层上下文管理**：工具结果落盘截断（层1）+ 结构化 LLM 摘要（层2，70% 警告 / 90% 压缩）+ 压缩熔断
- **JSONL 会话持久化**：追加写 O(1)、崩溃恢复、损坏行跳过、`--resume`
- **自动笔记**：每 5 轮按四分类增量沉淀长期记忆

## 快速开始

```bash
# 开发期：用 SDK 直接跑
dotnet build VestiCode.slnx
dotnet run --project src/VestiCode.Cli      # 首次运行会引导配置 API Key
dotnet test VestiCode.slnx                  # 58 单元测试

# 安装到本机
./install-local.sh                          # 之后任意目录可用 vesticode
```

首次运行若当前 Provider 未配置 API Key，会进入交互式向导，写入全局 `~/.vesticode/appsettings.json`。
也可用环境变量 `VESTICODE_API_KEY` 临时提供 Key。

## 配置一览

`~/.vesticode/`（全局）与 `./.vesticode/`（项目级，优先）同构，项目级覆盖/叠加全局：

| 内容 | 文件 / 目录 |
|------|------------|
| LLM 配置 | `appsettings.json`（仅 Provider/Key；其余用内置默认） |
| MCP | `mcp.yaml`（见 [example.mcp.yaml](config-examples/example.mcp.yaml)） |
| Hooks | `hooks.yaml`（见 [example.hooks.yaml](config-examples/example.hooks.yaml)） |
| 安全规则 | `security.json`（HITL 永久允许自动写入） |
| Skills | `skills/`（叠加内置） |
| Teams | `teams/*.json` |
| 指令 | 项目根 `VESTICODE.md` + 全局 `instructions.md`（`@include` 嵌套） |
| 记忆 | `notes/` `sessions/` `tool_results/` |

`VESTICODE_CONFIG=/path/to/config.json` 可显式指定配置文件（优先级最高）。

## 项目结构

```
VestiCode/
├── VestiCode.slnx                  # 解决方案
├── Directory.Build.props           # 统一构建设置（net10.0, Nullable, 警告即错误）
├── install-local.sh                # 跨平台构建 + 安装脚本
├── docs/                           # ARCHITECTURE / REFLECTION / TUTORIAL + 课程要求 PDF
├── config-examples/                # 各类配置示例（appsettings/mcp/hooks/team/role）
├── src/
│   ├── VestiCode.Core/             # 领域核心（无 UI 依赖）
│   │   ├── Agents/                 # ReAct 循环 + 事件流
│   │   ├── Llm/                    # Provider（OpenAI/Anthropic/DeepSeek）+ 工厂 + 重试
│   │   ├── Tools/                  # 内置工具 + 注册中心 + 执行器
│   │   ├── Conversation/           # 历史 / 截断 / 摘要 / 压缩
│   │   ├── Security/               # 黑名单 / 沙箱 / 策略 / HITL
│   │   ├── Prompts/                # 模块化 Prompt（嵌入资源）
│   │   ├── Skills/ SubAgents/ Teams/
│   │   ├── Hooks/ Mcp/ Worktree/
│   │   ├── Memory/ Notes/ Instructions/
│   │   ├── Commands/               # 17 条斜杠命令
│   │   └── DependencyInjection/    # DI 组合根
│   └── VestiCode.Cli/              # Generic Host + DI + 终端 UI
│       ├── Program.cs              # 配置分层 / 首次向导 / 启动编排
│       ├── SetupWizard.cs
│       └── Tui/                    # 内联 UI + Markdown 渲染 + 状态行
└── tests/VestiCode.UnitTests/      # xUnit
```

## 文档

- [架构设计文档 ARCHITECTURE.md](docs/ARCHITECTURE.md) — 架构图 / 工具设计 / 推理流程图
- [反思报告 REFLECTION.md](docs/REFLECTION.md) — AgentLoop 逐行解读 / 设计决策 / 问题诊断
- [使用教程 TUTORIAL.md](docs/TUTORIAL.md)

## 要求

- .NET SDK 10（`net10.0`）
- Git ≥ 2.30（Worktree 功能）
- Node / npx（仅当使用文件系统 MCP server 时）
