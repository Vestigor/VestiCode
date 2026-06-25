# 离线安装 vesticode（随发布压缩包分发）：把同目录的预构建 vesticode.exe 装到用户目录并配 PATH。
#
# 目标机无需 .NET 运行时、无需联网——exe 为自包含单文件。
# 用法（PowerShell）：
#   .\install.ps1
# 若提示脚本被禁用：
#   powershell -ExecutionPolicy Bypass -File .\install.ps1
$ErrorActionPreference = 'Stop'

$src = Join-Path $PSScriptRoot 'vesticode.exe'
if (-not (Test-Path $src)) {
    Write-Error "当前目录找不到 vesticode.exe，请在解压后的目录内运行本脚本。"
    exit 1
}

$dest = Join-Path $env:LOCALAPPDATA 'Programs\vesticode'
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item -Force $src (Join-Path $dest 'vesticode.exe')
Write-Host "已安装: $dest\vesticode.exe"

# 加入用户 PATH（幂等）
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if ($userPath -notlike "*$dest*") {
    $newPath = if ([string]::IsNullOrEmpty($userPath)) { $dest } else { "$userPath;$dest" }
    [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
    Write-Host "已加入用户 PATH：$dest（重开终端后生效）"
} else {
    Write-Host "PATH 已包含：$dest"
}

Write-Host "验证（新终端）：vesticode --version"
