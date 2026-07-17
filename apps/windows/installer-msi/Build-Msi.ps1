<#
  Akapen MSI をワンショットでビルドする。
  1. akapen-ffi (Rust) を release ビルド
  2. AkapenProbe を win-<arch> 自己完結 publish
  3. akapen.dll を publish\akapen_native.dll としてコピー
  4. EULA-ja.txt -> License.rtf 生成
  5. WiX で MSI をビルド

  リポジトリのルート (apps があるフォルダ) から、または本スクリプトの場所から実行する。

  -Arch x64|arm64 で対象アーキテクチャを切り替える (既定 x64)。arm64 は Rust の
  aarch64-pc-windows-msvc ターゲットと win-arm64 の自己完結 publish を用いる。
#>
param(
    [string]$Configuration = 'Release',
    [ValidateSet('x64', 'arm64')]
    [string]$Arch = 'x64',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$repo = (Resolve-Path (Join-Path $here '..\..\..')).Path
$version = '1.2.0'
$rid = "win-$Arch"
$publishDir = Join-Path $repo "apps\windows\AkapenProbe\bin\Release\net8.0\$rid\publish"

# Rust のターゲットトリプルと、そこにできる akapen.dll の位置。x64 はホスト
# 既定ターゲットなので --target を付けない (target\release\akapen.dll)。
if ($Arch -eq 'arm64') {
    $cargoTarget = 'aarch64-pc-windows-msvc'
    $rustDll = Join-Path $repo "target\$cargoTarget\release\akapen.dll"
} else {
    $cargoTarget = $null
    $rustDll = Join-Path $repo 'target\release\akapen.dll'
}

if (-not $SkipPublish) {
    Push-Location $repo
    try {
        if ($cargoTarget) {
            cargo build -p akapen-ffi --release --target $cargoTarget
        } else {
            cargo build -p akapen-ffi --release
        }
        if ($LASTEXITCODE -ne 0) { throw 'cargo build failed' }
        dotnet publish -c $Configuration -r $rid --self-contained true `
            (Join-Path $repo 'apps\windows\AkapenProbe\AkapenProbe.csproj')
        if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
        Copy-Item $rustDll (Join-Path $publishDir 'akapen_native.dll') -Force
    } finally { Pop-Location }
}

& (Join-Path $here 'Make-LicenseRtf.ps1')

$outputName = "Akapen-$version-win-$Arch"
dotnet build (Join-Path $here 'Akapen.wixproj') -c Release `
    "-p:PublishDir=$publishDir" "-p:Platform=$Arch" "-p:OutputName=$outputName"
if ($LASTEXITCODE -ne 0) { throw 'wix build failed' }

# -p:Platform を指定すると WiX SDK は bin\<Platform>\Release に出力する。
$msi = Join-Path $here "bin\$Arch\Release\$outputName.msi"
if (Test-Path $msi) {
    Write-Host "MSI: $msi"
    (Get-FileHash $msi -Algorithm SHA256).Hash
} else {
    throw "MSI not found at $msi"
}
