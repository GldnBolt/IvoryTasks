param([Parameter(Mandatory=$true)][string]$CertificateThumbprint, [string]$TimestampUrl = 'http://timestamp.digicert.com')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$kit = Get-ChildItem "${env:ProgramFiles(x86)}/Windows Kits/10/bin" -Directory | Where-Object { $_.Name -match '^10\.' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
& (Join-Path $kit.FullName 'x64/signtool.exe') sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 (Join-Path $projectRoot 'dist/IvoryTasks-1.0.0-x64.msix')
if ($LASTEXITCODE) { throw 'Signing failed. Certificate subject must match the manifest Publisher.' }
