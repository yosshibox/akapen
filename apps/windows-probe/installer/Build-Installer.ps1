param(
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [Parameter(Mandatory = $true)][string]$NativeDll,
    [Parameter(Mandatory = $true)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$staging = Join-Path $env:TEMP 'AkapenInstallerStaging'
$payload = Join-Path $env:TEMP 'AkapenInstallerPayload'
Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $payload -Recurse -Force -ErrorAction SilentlyContinue
New-Item $staging -ItemType Directory | Out-Null
New-Item $payload -ItemType Directory | Out-Null
Copy-Item (Join-Path $PublishDir '*') $payload -Recurse
Copy-Item $NativeDll (Join-Path $payload 'akapen_native.dll') -Force
Compress-Archive (Join-Path $payload '*') (Join-Path $staging 'Akapen.zip') -CompressionLevel Optimal

$install = @'
$ErrorActionPreference = 'Stop'
$target = Join-Path $env:LOCALAPPDATA 'Programs\Akapen'
New-Item $target -ItemType Directory -Force | Out-Null
Expand-Archive (Join-Path $PSScriptRoot 'Akapen.zip') $target -Force
$shell = New-Object -ComObject WScript.Shell
$desktop = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) 'Akapen.lnk'))
$desktop.TargetPath = Join-Path $target 'Akapen.exe'
$desktop.WorkingDirectory = $target
$desktop.Save()
$startMenuDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$startMenu = $shell.CreateShortcut((Join-Path $startMenuDir 'Akapen.lnk'))
$startMenu.TargetPath = Join-Path $target 'Akapen.exe'
$startMenu.WorkingDirectory = $target
$startMenu.Save()
Start-Process (Join-Path $target 'Akapen.exe')
'@
Set-Content (Join-Path $staging 'install.ps1') $install -Encoding UTF8
$installCmd = @'
@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
'@
Set-Content (Join-Path $staging 'install.cmd') $installCmd -Encoding ASCII

$files = Get-ChildItem $staging -File | Sort-Object Name
$strings = New-Object System.Collections.Generic.List[string]
$sourceEntries = New-Object System.Collections.Generic.List[string]
for ($i = 0; $i -lt $files.Count; $i++) {
    $strings.Add(('FILE{0}="{1}"' -f $i, $files[$i].Name))
    $sourceEntries.Add(('%FILE{0}%=' -f $i))
}
$outputDirectory = Split-Path $OutputPath -Parent
New-Item $outputDirectory -ItemType Directory -Force | Out-Null
$sed = @(
    '[Version]', 'Class=IEXPRESS', 'SEDVersion=3',
    '[Options]', 'PackagePurpose=InstallApp', 'ShowInstallProgramWindow=0',
    'HideExtractAnimation=1', 'UseLongFileName=1', 'InsideCompressed=0',
    'CAB_FixedSize=0', 'CAB_ResvCodeSigning=0', 'RebootMode=N', 'CompressionType=MSZIP',
    'InstallPrompt=%InstallPrompt%', 'DisplayLicense=%DisplayLicense%', 'FinishMessage=%FinishMessage%',
    'TargetName=%TargetName%', 'FriendlyName=%FriendlyName%',
    'AppLaunched=%AppLaunched%', 'PostInstallCmd=%PostInstallCmd%',
    'AdminQuietInstCmd=%AdminQuietInstCmd%', 'UserQuietInstCmd=%UserQuietInstCmd%',
    'SourceFiles=SourceFiles',
    '[SourceFiles]', ('SourceFiles0={0}\' -f $staging),
    '[SourceFiles0]'
) + $sourceEntries + @('[Strings]', 'InstallPrompt=', 'DisplayLicense=', 'FinishMessage=',
    ('TargetName={0}' -f $OutputPath), 'FriendlyName=Akapen Setup', 'AppLaunched=install.cmd',
    'PostInstallCmd=<None>', 'AdminQuietInstCmd=', 'UserQuietInstCmd=install.cmd') + $strings
$sedPath = Join-Path $env:TEMP 'AkapenSetup.sed'
Set-Content $sedPath $sed -Encoding ASCII
& "$env:WINDIR\System32\iexpress.exe" /N /Q $sedPath
if (-not (Test-Path $OutputPath)) { throw 'IExpress did not produce AkapenSetup.exe' }
Write-Host "Akapen installer: $OutputPath"
