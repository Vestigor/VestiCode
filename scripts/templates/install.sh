#!/usr/bin/env bash
# 离线安装 vesticode（随发布压缩包分发）：把同目录的预构建二进制装到 ~/.local/bin。
#
# 目标机无需 .NET 运行时、无需联网——二进制为自包含单文件。
# 用法：
#   ./install.sh                 # 装到 ~/.local/bin
#   VESTICODE_BIN_DIR=/usr/local/bin sudo ./install.sh   # 自定义安装目录
#
# 关键：先 rm 旧文件（换新 inode）再拷，避免"原地覆盖已签名二进制"导致
# macOS 内核签名页缓存失效 → 启动即被 SIGKILL（Code Signature Invalid）。
set -euo pipefail
cd "$(dirname "$0")"

BIN="vesticode"
DEST_DIR="${VESTICODE_BIN_DIR:-$HOME/.local/bin}"
DEST="$DEST_DIR/$BIN"

if [ ! -f "$BIN" ]; then
  echo "错误：当前目录找不到 $BIN，请在解压后的目录内运行本脚本。" >&2
  exit 1
fi

mkdir -p "$DEST_DIR"
rm -f "$DEST"                         # 换新 inode（必须，别原地覆盖）
cp "$BIN" "$DEST"
chmod +x "$DEST"

# macOS：ad-hoc 重签名 + 去隔离属性（其它平台跳过）
if [ "$(uname -s)" = "Darwin" ]; then
  xattr -dr com.apple.quarantine "$DEST" 2>/dev/null || true
  codesign --force --sign - "$DEST" 2>/dev/null || true
fi

echo "已安装: $DEST"

# ---- 自动配置 PATH 环境变量 --------------------------------------------------
# 若 DEST_DIR 已在 PATH 中则跳过；否则把 export 行写入当前 shell 的 rc 文件（幂等）。
# 可用 VESTICODE_NO_PATH=1 关闭自动写入（只提示，不改 rc）。
configure_path() {
  case ":$PATH:" in
    *":$DEST_DIR:"*) return 0 ;;   # 已在 PATH，无需处理
  esac

  if [ "${VESTICODE_NO_PATH:-0}" = "1" ]; then
    echo "提示：$DEST_DIR 不在 PATH。请手动加入后重开终端：" >&2
    echo "      export PATH=\"$DEST_DIR:\$PATH\"" >&2
    return 0
  fi

  # 选 rc 文件：优先按登录 shell；找不到就退回到常见文件。
  local rc shell_name
  shell_name="$(basename "${SHELL:-}")"
  case "$shell_name" in
    zsh)  rc="${ZDOTDIR:-$HOME}/.zshrc" ;;
    bash) [ -f "$HOME/.bashrc" ] && rc="$HOME/.bashrc" || rc="$HOME/.bash_profile" ;;
    fish)
      # fish 语法不同，单独处理
      local fish_cfg="$HOME/.config/fish/config.fish"
      mkdir -p "$(dirname "$fish_cfg")"
      if ! grep -qs "vesticode PATH" "$fish_cfg" 2>/dev/null; then
        {
          echo ""
          echo "# vesticode PATH"
          echo "set -gx PATH \"$DEST_DIR\" \$PATH"
        } >> "$fish_cfg"
        echo "已写入 PATH 到 $fish_cfg（重开终端生效）"
      else
        echo "PATH 已在 $fish_cfg 配置过"
      fi
      return 0 ;;
    *)    rc="$HOME/.profile" ;;
  esac

  local marker="# vesticode PATH"
  local line="export PATH=\"$DEST_DIR:\$PATH\""
  if [ -f "$rc" ] && grep -qF "$marker" "$rc"; then
    echo "PATH 已在 $rc 配置过"
  else
    { echo ""; echo "$marker"; echo "$line"; } >> "$rc"
    echo "已写入 PATH 到 $rc"
  fi
  echo "立即生效：source \"$rc\"   （或重开终端）"
}
configure_path

echo "验证：vesticode --version"
