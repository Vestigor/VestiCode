# VestiCode 安装说明 · Windows（x64）

自包含单文件：**无需安装 .NET**，解压即用。

## 安装
在当前（解压后的）目录内打开 **PowerShell**，执行：
```powershell
.\install.ps1
```
若提示脚本被禁用：
```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```
脚本会自动：
- 把 `vesticode.exe` 装到 `%LOCALAPPDATA%\Programs\vesticode`
- 写入用户 PATH 环境变量（重开终端生效）

## 验证
重开终端后：
```
vesticode --version
```

## 首次使用
首次运行进入配置向导（选择 LLM 提供方 / 模型 / 填 API Key）。
随包目录 `config-examples\` 内有 LLM / MCP / Hooks / Teams / Role 的示例配置。

## 显示乱码 / 看到“？”？
程序已自动把控制台切到 UTF-8。若个别字符仍显示异常，建议：
- 使用 **Windows Terminal**（而非老版 cmd），渲染 Unicode 更好；
- 或在 cmd 里先执行 `chcp 65001`，并改用支持 Unicode 的等宽字体（Cascadia Mono / Consolas）。
