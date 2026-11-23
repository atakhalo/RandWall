# 发布脚本：生成可分发的 RandWall 包
#
#   .\scripts\publish.ps1 framework      依赖 .NET 8 桌面运行时（无 8 时自动用 9/10），整个目录一起分发（体积最小）
#   .\scripts\publish.ps1 single         依赖 .NET 8 桌面运行时（无 8 时自动用 9/10），单个 exe
#   .\scripts\publish.ps1 selfcontained  自带运行时，单个 exe，目标机不需要安装 .NET（体积最大）
#
param(
    [ValidateSet('framework', 'single', 'selfcontained')]
    [string]$Mode = 'framework',

    [string]$Output
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\RandWall\RandWall.csproj'

if (-not $Output) {
    $Output = switch ($Mode) {
        'framework' { Join-Path $root 'publish' }
        'single' { Join-Path $root 'publish-single' }
        'selfcontained' { Join-Path $root 'publish-standalone' }
    }
}

$common = @(
    'publish', $project,
    '-c', 'Release',
    '-r', 'win-x64',
    '-o', $Output,
    '--nologo'
)

switch ($Mode) {
    'framework' {
        dotnet @common --self-contained false
    }
    'single' {
        dotnet @common --self-contained false '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true'
    }
    'selfcontained' {
        dotnet @common --self-contained true '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true'
    }
}

if ($LASTEXITCODE -ne 0) {
    throw "发布失败，退出码 $LASTEXITCODE"
}

$exe = Join-Path $Output 'RandWall.exe'
if (Test-Path $exe) {
    $size = (Get-Item $exe).Length / 1MB
    Write-Host ("完成：{0}（{1:N1} MB）" -f $exe, $size)
} else {
    Write-Host "完成：$Output"
}
