$ErrorActionPreference = 'Stop'
$stage = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'staging-path.txt') -Raw).Trim()
$executable = Join-Path $stage 'App/IvoryTasks.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'Portable files were moved. Rebuild or launch IvoryTasks.exe inside the staging App folder.' }
Start-Process -FilePath $executable
