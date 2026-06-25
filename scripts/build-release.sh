#!/usr/bin/env bash
# 一键打发布包：在本机（含 M 系 Mac）交叉编译三平台自包含单文件，
# 分别打成压缩包（内含离线安装脚本 + 配置示例）到 dist/release/。
#
# 自包含单文件 = 目标机无需装 .NET、无需联网即可安装运行。
#
# 用法：
#   scripts/build-release.sh                 # 版本号取自 csproj 的 <Version>
#   scripts/build-release.sh 0.2.0           # 显式指定版本号
#   RIDS="osx-arm64 linux-x64" scripts/build-release.sh   # 只打部分平台
set -euo pipefail
cd "$(dirname "$0")/.."   # 仓库根

PROJ="src/VestiCode.Cli"
CSPROJ="$PROJ/VestiCode.Cli.csproj"

# 版本号：参数优先，否则读 csproj <Version>
VERSION="${1:-$(grep -oE '<Version>[^<]+' "$CSPROJ" | head -1 | sed 's/<Version>//')}"
[ -n "$VERSION" ] || { echo "无法确定版本号（csproj 无 <Version>，且未传参）" >&2; exit 1; }

# 目标平台（mac M 系 / Windows / Linux）；可用环境变量 RIDS 覆盖
RIDS="${RIDS:-osx-arm64 win-x64 linux-x64}"

STAGE="dist/release"
PUBROOT="dist/publish"
rm -rf "$STAGE" "$PUBROOT"
mkdir -p "$STAGE"

echo "== vesticode 发布打包  version=$VERSION =="

for RID in $RIDS; do
  echo
  echo "== 构建 $RID =="
  OUT="$PUBROOT/$RID"
  dotnet publish "$PROJ" -c Release -r "$RID" --self-contained \
    -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false \
    -p:Version="$VERSION" -o "$OUT" >/dev/null

  NAME="vesticode-$VERSION-$RID"
  PKGDIR="$STAGE/$NAME"
  mkdir -p "$PKGDIR"

  # 二进制 + 平台安装脚本
  if [[ "$RID" == win-* ]]; then
    cp "$OUT/vesticode.exe" "$PKGDIR/"
    # PowerShell 5.x 按系统 ANSI 代码页（中文系统=GBK）读无 BOM 的 .ps1，中文会乱码
    # 导致解析失败；写出 UTF-8 BOM 确保正确解码。
    printf '\xEF\xBB\xBF' > "$PKGDIR/install.ps1"
    cat scripts/templates/install.ps1 >> "$PKGDIR/install.ps1"
  else
    cp "$OUT/vesticode" "$PKGDIR/"
    chmod +x "$PKGDIR/vesticode"
    cp scripts/templates/install.sh "$PKGDIR/"
    chmod +x "$PKGDIR/install.sh"
  fi

  # 随包“安装 README”（分平台，非项目 README）：替换版本/RID 占位后写入
  case "$RID" in
    osx-*)   RM="scripts/templates/README.osx.md" ;;
    win-*)   RM="scripts/templates/README.win.md" ;;
    *)       RM="scripts/templates/README.linux.md" ;;
  esac
  sed -e "s/__VERSION__/$VERSION/g" -e "s/__RID__/$RID/g" "$RM" > "$PKGDIR/README.md"

  # 配置示例
  cp -R config-examples "$PKGDIR/config-examples"

  # 打包：mac/linux → tar.gz；windows → zip
  ( cd "$STAGE"
    if [[ "$RID" == win-* ]]; then
      rm -f "$NAME.zip"; zip -qr "$NAME.zip" "$NAME"
    else
      tar -czf "$NAME.tar.gz" "$NAME"
    fi )
  rm -rf "$PKGDIR"
done

echo
echo "== 完成，产物在 $STAGE/ =="
( cd "$STAGE" && ls -1 *.tar.gz *.zip 2>/dev/null )

# 生成 SHA256 校验文件，便于发布与离线校验
( cd "$STAGE"
  if command -v shasum >/dev/null 2>&1; then
    shasum -a 256 *.tar.gz *.zip 2>/dev/null > SHA256SUMS.txt || true
  elif command -v sha256sum >/dev/null 2>&1; then
    sha256sum *.tar.gz *.zip 2>/dev/null > SHA256SUMS.txt || true
  fi )
[ -f "$STAGE/SHA256SUMS.txt" ] && echo "校验和: $STAGE/SHA256SUMS.txt"
