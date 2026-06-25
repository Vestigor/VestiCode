#!/usr/bin/env bash
# 本机安装/更新 vesticode：自动探测平台 → 构建自包含单文件 → 安全替换到 ~/.local/bin。
#
# 支持：macOS(arm64/x64)、Linux(x64/arm64)；原生 Windows 请在 WSL / Git-Bash 中运行。
# 用法：
#   ./install-local.sh                # 自动探测当前平台 RID
#   ./install-local.sh linux-x64      # 显式指定 RID（osx-arm64 / osx-x64 / win-x64 / linux-x64 …）
#
# 关键：先 rm 旧文件（换新 inode）再拷，避免"原地覆盖已签名二进制"导致
# macOS 内核签名页缓存失效 → 启动即被 SIGKILL（Code Signature Invalid）。
set -euo pipefail
cd "$(dirname "$0")"

# ---- 1) 探测 RID（可用第一个参数覆盖）----------------------------------------
detect_rid() {
  local os arch
  case "$(uname -s)" in
    Darwin)               os=osx ;;
    Linux)                os=linux ;;
    MINGW*|MSYS*|CYGWIN*) os=win ;;
    *) echo "不支持的系统: $(uname -s)" >&2; exit 1 ;;
  esac
  case "$(uname -m)" in
    arm64|aarch64) arch=arm64 ;;
    x86_64|amd64)  arch=x64 ;;
    *) echo "不支持的架构: $(uname -m)" >&2; exit 1 ;;
  esac
  echo "${os}-${arch}"
}

RID="${1:-$(detect_rid)}"

# Windows 产物带 .exe 后缀。
BIN="vesticode"
case "$RID" in win-*) BIN="vesticode.exe" ;; esac

OUT="dist/$RID"

# ---- 2) 构建自包含单文件 -----------------------------------------------------
echo "== 构建 $RID =="
dotnet publish src/VestiCode.Cli -c Release -r "$RID" --self-contained \
  -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false \
  -o "$OUT" >/dev/null

# ---- 3) 安装到 ~/.local/bin --------------------------------------------------
DEST_DIR="$HOME/.local/bin"
DEST="$DEST_DIR/$BIN"
mkdir -p "$DEST_DIR"
rm -f "$DEST"                         # 换新 inode（必须，别原地覆盖）
cp "$OUT/$BIN" "$DEST"
chmod +x "$DEST"

# ---- 4) macOS：ad-hoc 重签名（其它平台跳过）----------------------------------
if [ "$(uname -s)" = "Darwin" ]; then
  codesign --force --sign - "$DEST" 2>/dev/null || true
fi

echo "已安装: $DEST"
if [ "$(uname -s)" = "Darwin" ]; then
  codesign --verify --verbose "$DEST" 2>&1 | head -1 || true
fi

# ---- 5) PATH 提示 ------------------------------------------------------------
case ":$PATH:" in
  *":$DEST_DIR:"*) ;;
  *) echo "提示：$DEST_DIR 不在 PATH 中。请加入后重开终端：" >&2
     echo "      export PATH=\"$DEST_DIR:\$PATH\"" >&2 ;;
esac
