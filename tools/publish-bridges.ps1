# 把 VST 桥发布到应用目录旁边。
#
# 为什么需要这一步：桥是独立的 exe，不是 ProjectReference（位数/平台与宿主不同，
# 也不能让宿主带 x86 目标）。运行时要求固定布局：
#
#   <app>/bridge/<rid>/TuneLab.ExperimentalPatch.VstBridge.x64         （64 位，VST2 + VST3）
#   <app>/bridge/win-x86/TuneLab.ExperimentalPatch.VstBridge.exe       （32 位，仅 VST2）
#
# 目录必须按 RID 分开：hostfxr / coreclr 同名不同平台/架构，混放必炸。
# 又因为多数机器只装了 x64 的 .NET 运行时，桥一律 --self-contained。
#
# 用法：
#   pwsh tools/publish-bridges.ps1                                  # Windows 开发默认：win-x86 + win-x64
#   pwsh tools/publish-bridges.ps1 -Configuration Release
#   pwsh tools/publish-bridges.ps1 -AppOutput "D:\TuneLab"
#   pwsh tools/publish-bridges.ps1 -AppOutput workspace -Runtime linux-x64   # CI：按目标 RID 发一份
#
# [P5.3] -Runtime 给了就只发"该目标平台需要的桥"：
#   · 64 位桥：目标 RID 各一份；
#   · Windows 目标额外补一份 win-x86 桥（32 位 VST2 只能在 x86 进程里加载）；
#   · 非 Windows 目标没有 32 位 VST（VST2 跨平台不在范围内），只发 64 位桥。

param(
    [string]$Configuration = "Debug",
    [string]$AppOutput = "",
    [string]$Runtime = "",
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

$bridges = @()

if (-not $Runtime) {
    # 没给目标 RID：保持老的 Windows 开发默认（两个位数都发）。
    $bridges = @(
        @{ Project = "ExperimentalPatch/VstBridge/VstBridge.csproj";         Rid = "win-x86"; Folder = "bridge/win-x86" },
        @{ Project = "ExperimentalPatch/VstBridge.X64/VstBridge.X64.csproj"; Rid = "win-x64"; Folder = "bridge/win-x64" }
    )
}
elseif ($Runtime -like "win-*") {
    $bridges += @{ Project = "ExperimentalPatch/VstBridge.X64/VstBridge.X64.csproj"; Rid = $Runtime; Folder = "bridge/$Runtime" }

    if ($Runtime -ne "win-x86") {
        # Windows 宿主同时要能加载 32 位 VST2：x86 桥固定 win-x86。
        $bridges += @{ Project = "ExperimentalPatch/VstBridge/VstBridge.csproj"; Rid = "win-x86"; Folder = "bridge/win-x86" }
    }
}
else {
    # linux-* / osx-*：只有 64 位桥。
    $bridges += @{ Project = "ExperimentalPatch/VstBridge.X64/VstBridge.X64.csproj"; Rid = $Runtime; Folder = "bridge/$Runtime" }
}

foreach ($bridge in $bridges) {
    $project = Join-Path $repo $bridge.Project
    $target = Join-Path $AppOutput $bridge.Folder

    Write-Host "publishing $($bridge.Project) [$($bridge.Rid)] -> $target"

    & $Dotnet publish $project `
        -c $Configuration `
        -r $bridge.Rid `
        --self-contained true `
        -p:PublishSingleFile=false `
        -o $target `
        --nologo -v:q

    if ($LASTEXITCODE -ne 0) {
        throw "publish failed: $($bridge.Project) [$($bridge.Rid)]"
    }
}

Write-Host "done. bridges are under $AppOutput/bridge/"
