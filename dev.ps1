# Pinta 本地开发脚本
# 用法（在仓库根目录 powershell 中执行）：
#   .\dev.ps1           # 仅编译
#   .\dev.ps1 -Run      # 编译并运行
#   .\dev.ps1 -Clean    # 清理后编译
#   .\dev.ps1 -SelfContained  # 编译并发布为自包含（可分发的 exe）

[CmdletBinding()]
param(
    [switch]$Run,
    [switch]$Clean,
    [switch]$SelfContained,
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

# 允许 framework-dependent 的 apphost roll-forward 到更新的 .NET 10 运行时
$env:DOTNET_ROLL_FORWARD = 'LatestMinor'
$env:DOTNET_ROLL_FORWARD_TO_PRERELEASE = '1'

# 添加 MSYS2 的 bin 目录到 PATH（用于 msgfmt 等工具）
if (Test-Path 'C:\msys64\usr\bin') {
    $env:PATH = 'C:\msys64\usr\bin;' + $env:PATH
}

$msysRoot = 'C:\msys64\clang64'
$mingwFolder = if (Test-Path $msysRoot) { $msysRoot } else { '' }

# 检查 MSYS2 是否安装（如果没有 self-contained）
if (-not $SelfContained -and [string]::IsNullOrEmpty($mingwFolder)) {
    Write-Host ''
    Write-Host '! WARNING: MSYS2 not found at C:\msys64\clang64' -ForegroundColor Yellow
    Write-Host '! Pinta needs GTK4 / libadwaita libraries from MSYS2 to run.' -ForegroundColor Yellow
    Write-Host '! Follow WINDOWS_SETUP.md to install MSYS2 first, or use -SelfContained to build a full release.' -ForegroundColor Yellow
    Write-Host ''
}

# Stop only Pinta processes from a previous run before build outputs are replaced.
$pintaProcesses = Get-CimInstance Win32_Process |
    Where-Object {
        $_.Name -eq 'Pinta.exe' -or
        ($_.Name -eq 'dotnet.exe' -and $_.CommandLine -match '(?i)Pinta\.dll(?:\s|"|$)')
    }

if ($pintaProcesses) {
    Write-Host '>>> Stopping previous Pinta process...' -ForegroundColor Cyan
    $pintaProcesses | ForEach-Object {
        Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
    }
}

if ($Clean) {
    Write-Host '>>> Cleaning...' -ForegroundColor Cyan
    dotnet clean Pinta.sln -c $Configuration -v quiet
    if (Test-Path 'build') { Remove-Item 'build' -Recurse -Force }
}

$commonArgs = @(
    'Pinta.sln',
    '-c', $Configuration,
    "-p:MinGWFolder=$mingwFolder",
    '-p:BuildTranslations=true'
)

if ($SelfContained) {
    Write-Host '>>> Publishing self-contained (this takes a while)...' -ForegroundColor Cyan
    dotnet publish Pinta/Pinta.csproj `
        -c $Configuration `
        -r win-x64 `
        --self-contained true `
        -p:MinGWFolder=$mingwFolder `
        -p:BuildTranslations=true `
        -p:PublishDir=build/publish
    $exe = Resolve-Path 'build/publish/Pinta.exe'
} else {
    Write-Host '>>> Building framework-dependent (fast)...' -ForegroundColor Cyan
    dotnet build @commonArgs
    $exe = Resolve-Path "build/bin/Pinta.exe"
}

Write-Host ">>> Output: $exe" -ForegroundColor Green

if ($Run) {
    Write-Host '>>> Launching Pinta (using dotnet.exe directly)...' -ForegroundColor Cyan
    if ($SelfContained) {
        $exeDir = Split-Path $exe
        Push-Location $exeDir
        & $exe
        Pop-Location
    } else {
        # Bypass the apphost (Pinta.exe), use dotnet.exe to load Pinta.dll directly (most reliable roll-forward)
        $dllPath = Join-Path (Split-Path $exe -Parent) 'Pinta.dll'
        Push-Location (Split-Path $exe -Parent)
        if (Test-Path $dllPath) {
            & dotnet $dllPath
        } else {
            & $exe
        }
        Pop-Location
    }
}
