# scripts/ — 发布打包

一键在本机（含 Apple Silicon Mac）交叉编译多平台**自包含单文件**，分别打成压缩包（内含离线安装脚本 + 分平台安装 README + 配置示例），用于 GitHub Release 分发。

自包含单文件 = 目标机**无需安装 .NET、无需联网**即可安装运行。

## 目录

| 文件 | 作用 |
|------|------|
| `build-release.sh` | 主脚本：交叉编译各 RID → 打包到 `dist/release/` → 生成 `SHA256SUMS.txt` |
| `templates/install.sh` | 随 mac/linux 包分发的离线安装脚本（装到 `~/.local/bin`，自动配置 PATH） |
| `templates/install.ps1` | 随 Windows 包分发的安装脚本（装到 `%LOCALAPPDATA%\Programs\vesticode`，写用户 PATH） |
| `templates/README.osx.md` | 包内安装说明 · macOS（打包时替换 `__VERSION__`/`__RID__`） |
| `templates/README.linux.md` | 包内安装说明 · Linux |
| `templates/README.win.md` | 包内安装说明 · Windows |

## 用法

```bash
# 版本号取自 src/VestiCode.Cli/VestiCode.Cli.csproj 的 <Version>
scripts/build-release.sh

# 显式指定版本号
scripts/build-release.sh 0.1.0

# 只打部分平台（便于本地快速验证）
RIDS="osx-arm64 linux-x64" scripts/build-release.sh
```

默认平台：`osx-arm64`（mac M 系）、`win-x64`、`linux-x64`。

## 产物

打到 `dist/release/`（已 gitignore，不入库）：

```
vesticode-<版本>-osx-arm64.tar.gz
vesticode-<版本>-linux-x64.tar.gz
vesticode-<版本>-win-x64.zip
SHA256SUMS.txt
```

每个压缩包内含：平台二进制、对应安装脚本（`install.sh` / `install.ps1`）、平台 `README.md`、`config-examples/`。

## 发布到 GitHub Release

```bash
# 方式 A：gh CLI（需先安装并登录）
gh release create VestiCode_v0.1.0 dist/release/*.tar.gz dist/release/*.zip dist/release/SHA256SUMS.txt \
  --title "VestiCode v0.1.0" --notes "..."

# 方式 B：网页手动上传 dist/release/ 下的所有文件到 Releases 页面
```

## 实现要点

- **交叉编译**：`.NET` 自包含发布支持从一台机器产出其它平台二进制（不开 ReadyToRun/AOT），无需各平台机器。
- **mac 签名**：`install.sh` 对二进制去隔离属性 + ad-hoc 重签名，规避 Gatekeeper 拦截。
- **Windows 编码**：打包时为 `install.ps1` 写入 UTF-8 BOM，避免中文系统 PowerShell 5.x 按 GBK 解析导致乱码/报错。
- **PATH 自动配置**：`install.sh` 按当前 shell 把 `export PATH` 写入对应 rc 文件（zsh/bash/fish），幂等。
