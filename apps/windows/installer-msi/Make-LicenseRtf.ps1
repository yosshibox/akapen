<#
  EULA-ja.txt (UTF-8) を WixUI が表示する License.rtf に変換する。
  日本語が化けないよう、非 ASCII 文字はすべて RTF の \uN? Unicode エスケープで
  出力する (コードページに依存しない)。フォントは Yu Gothic (fcharset128)。
#>
param(
    [string]$Source = (Join-Path $PSScriptRoot '..\installer\EULA-ja.txt'),
    [string]$Output = (Join-Path $PSScriptRoot 'License.rtf')
)

$ErrorActionPreference = 'Stop'
$text = [System.IO.File]::ReadAllText($Source, [System.Text.Encoding]::UTF8)
$text = $text -replace "`r`n", "`n" -replace "`r", "`n"

$sb = New-Object System.Text.StringBuilder
[void]$sb.Append('{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fnil\fcharset128 Yu Gothic;}}')
[void]$sb.Append('\f0\fs18 ')

foreach ($ch in $text.ToCharArray()) {
    $code = [int]$ch
    switch ($ch) {
        "`n" { [void]$sb.Append('\par' + "`r`n"); continue }
        '\'  { [void]$sb.Append('\\'); continue }
        '{'  { [void]$sb.Append('\{'); continue }
        '}'  { [void]$sb.Append('\}'); continue }
    }
    if ($code -ge 32 -and $code -le 126) {
        [void]$sb.Append($ch)
    } else {
        # RTF \u は符号付き 16bit。65535 超は char 単位 (サロゲート) で来る。
        $signed = if ($code -gt 32767) { $code - 65536 } else { $code }
        [void]$sb.Append('\u' + $signed + '?')
    }
}
[void]$sb.Append('}')

[System.IO.File]::WriteAllText($Output, $sb.ToString(), (New-Object System.Text.ASCIIEncoding))
Write-Host "Wrote $Output"
