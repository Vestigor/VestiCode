# VestiCode 安装说明 · macOS（Apple Silicon）

自包含单文件：**无需安装 .NET**，解压即用。

## 安装
在当前（解压后的）目录内执行：
```bash
./install.sh
```
脚本会自动：
- 把 `vesticode` 装到 `~/.local/bin`
- 去除隔离属性 + ad-hoc 重签名（绕过 Gatekeeper 拦截）
- 把 PATH 写入你的 shell 配置（zsh→`~/.zshrc` / bash→`~/.bashrc` / fish）

## 验证
```bash
source ~/.zshrc        # 或重开终端
vesticode --version
```

## 首次使用
首次运行进入配置向导（选择 LLM 提供方 / 模型 / 填 API Key）。
随包目录 `config-examples/` 内有 LLM / MCP / Hooks / Teams / Role 的示例配置。

## 选项 / 排错
- 自定义安装目录：`VESTICODE_BIN_DIR=/usr/local/bin sudo ./install.sh`
- 不自动改 PATH：`VESTICODE_NO_PATH=1 ./install.sh`
- 仍被 Gatekeeper 拦截时手动放行：
  ```bash
  xattr -dr com.apple.quarantine ~/.local/bin/vesticode
  codesign --force --sign - ~/.local/bin/vesticode
  ```
