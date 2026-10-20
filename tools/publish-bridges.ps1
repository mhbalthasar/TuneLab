# 把 VST2 桥（两个位数）发布到应用目录旁边。
#
# 为什么需要这一步：桥是独立的 exe，不是 ProjectReference（位数不同、
# 也不能让宿主带 x86 目标）。运行时要求固定布局：
#
#   <app>/bridge/win-x86/TuneLab.ExperimentalPatch.VstBridge.exe
#   <app>/bridge/win-x64/TuneLab.ExperimentalPatch.VstBridge.x64.exe
#
# 两个目录必须分开：hostfxr.dll / coreclr.dll 同名不同架构，混放必炸。
# 又因为多数机器只装了 x64 的 .NET 运行时（x86 的通常没有），
# x86 那份必须 --self-contained。x64 也按同样的方式发，保持一致。
#
# 用法：
#   pwsh tools/publish-bridges.ps1                     # 发到 TuneLab/bin/Debug/net8.0
#   pwsh tools/publish-bridges.ps1 -Configuration Release
#   pwsh tools/publish-bridges.ps1 -AppOutput "D:\TuneLab"

param(
    [string]$Configuration = "Debug",
    [string]$AppOutput = "",
    [string]$Dotnet = "dotnet"
)

$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
if (-not $AppOutput) {
    $AppOutput = Join-Path $repo "TuneLab/bin/$Configuration/net8.0"
}

if (-not (Test-Path $AppOutput)) {
    throw "app output directory not found: $AppOutput (build TuneLab first, or pass -AppOutput)"
}

$bridges = @(
    @{ Project = "ExperimentalPatch/VstBridge/VstBridge.csproj";       Rid = "win-x86"; Folder = "bridge/win-x86" },
    @{ Project = "ExperimentalPatch/VstBridge.X64/VstBridge.X64.csproj"; Rid = "win-x64"; Folder = "bridge/win-x64" }
)

foreach ($bridge in $bridges) {
    $project = Join-Path $repo $bridge.Project
    $target = Join-Path $AppOutput $bridge.Folder

    Write-Host "publishing $($bridge.Project) -> $target"

    & $Dotnet publish $project `
        -c $Configuration `
        -r $bridge.Rid `
        --self-contained true `
        -p:PublishSingleFile=false `
        -o $target `
        --nologo -v:q

    if ($LASTEXITCODE -ne 0) {
        throw "publish failed: $($bridge.Project)"
    }
}

Write-Host "done. bridges are under $AppOutput/bridge/"
