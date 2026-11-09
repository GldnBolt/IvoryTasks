$ErrorActionPreference = 'Stop'
$package = Join-Path $PSScriptRoot 'IvoryTasks-1.0.0-x64.msix'
$signature = Get-AuthenticodeSignature -LiteralPath $package
if ($signature.Status -ne 'Valid') { throw 'The package needs a trusted code-signing signature. See README Installation. This script never changes certificate trust or security settings.' }
Add-AppxPackage -Path $package
Write-Host 'Ivory Tasks installed. Open it from Start, then add Ivory · My tasks in Windows Widgets.'
