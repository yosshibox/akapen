<#
  Akapen MSI をワンショットでビルドする。
  1. akapen-ffi (Rust) を release ビルド
  2. AkapenProbe を win-x64 自己完結 publish
  3. akapen.dll を publish\akapen_native.dll としてコピー
  4. EULA-ja.txt -> License.rtf 生成
  5. WiX で MSI をビルド

  リポジトリのルート (apps があるフォルダ) から、または本スクリプトの場所から実行する。
#>
param(
    [string]$Configuration = 'Release',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$repo = (Resolve-Path (Join-Path $here '..\..\..')).Path
$publishDir = Join-Path $repo 'apps\windows\AkapenProbe\bin\Release\net8.0\win-x64\publish'

if (-not $SkipPublish) {
    Push-Location $repo
    try {
        cargo build -p akapen-ffi --release
        if ($LASTEXITCODE -ne 0) { throw 'cargo build failed' }
        dotnet publish -c $Configuration -r win-x64 --self-contained true `
            (Join-Path $repo 'apps\windows\AkapenProbe\AkapenProbe.csproj')
        if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
        Copy-Item (Join-Path $repo 'target\release\akapen.dll') `
                  (Join-Path $publishDir 'akapen_native.dll') -Force
    } finally { Pop-Location }
}

& (Join-Path $here 'Make-LicenseRtf.ps1')

dotnet build (Join-Path $here 'Akapen.wixproj') -c Release "-p:PublishDir=$publishDir"
if ($LASTEXITCODE -ne 0) { throw 'wix build failed' }

$msi = Join-Path $here 'bin\Release\Akapen-1.1.2-win-x64.msi'
if (Test-Path $msi) {
    Write-Host "MSI: $msi"
    (Get-FileHash $msi -Algorithm SHA256).Hash
} else {
    throw "MSI not found at $msi"
}
