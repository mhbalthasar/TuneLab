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
#   pwsh tools/publish-bridges.ps1 -YsfxNativeDir "..\ysfx_lib\artifacts"  # 同时补 JSFX 运行时
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
    [string]$Dotnet = "dotnet",
    # ysfx_lib 的产物目录（可选）。里面可以直接放 ysfx.tlhost.dll /
    # libysfx.tlhost.so / libysfx.tlhost.dylib，也可以按 <rid>/ 分子目录。
    # 没给时退回 ExperimentalPatch/InsertHost/Native/<rid>/。
    [string]$YsfxNativeDir = ""
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

    # JSFX 的 native 运行时（ysfx）不是 .NET 程序集，不会跟着 publish 出来。
    # 32 位桥不声明 JSFX；只给 64 位桥补。
    if ($bridge.Rid -ne "win-x86") {
        $ysfxName = if ($bridge.Rid -like "win-*") { "ysfx.tlhost.dll" }
            elseif ($bridge.Rid -like "osx-*") { "libysfx.tlhost.dylib" }
            else { "libysfx.tlhost.so" }

        $candidates = @()

        if ($YsfxNativeDir) {
            $candidates += Join-Path $YsfxNativeDir $ysfxName
            $candidates += Join-Path (Join-Path $YsfxNativeDir $bridge.Rid) $ysfxName
        }

        $candidates += Join-Path $repo "ExperimentalPatch/InsertHost/Native/$($bridge.Rid)/$ysfxName"

        $ysfx = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1

        if ($ysfx) {
            Copy-Item $ysfx $target -Force
            Write-Host "  + $ysfxName (from $ysfx)"
        }
        else {
            Write-Warning "ysfx runtime not found for $($bridge.Rid); JSFX will stay unavailable until $ysfxName is placed in $target"
        }
    }
}

Write-Host "done. bridges are under $AppOutput/bridge/"
