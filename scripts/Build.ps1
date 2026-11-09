param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    dotnet test tests/Ivory.Tests/Ivory.Tests.csproj -c $Configuration -v minimal
    if ($LASTEXITCODE) { throw 'Tests failed.' }
    $dist = Join-Path $projectRoot 'dist'
    New-Item -ItemType Directory -Force $dist | Out-Null
    $stage = Join-Path $dist ('staging-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force $stage | Out-Null
    foreach ($target in @(@('Ivory.Desktop','App'),@('Ivory.Widget','Widget'))) {
        dotnet publish "src/$($target[0])/$($target[0]).csproj" -c $Configuration -p:Platform=x64 -r win-x64 --self-contained true -p:PublishTrimmed=false -p:PublishSingleFile=false -o (Join-Path $stage $target[1]) -v minimal
        if ($LASTEXITCODE) { throw "Publish failed: $($target[0])" }
    }
    Copy-Item -LiteralPath 'packaging/AppxManifest.xml' -Destination (Join-Path $stage 'AppxManifest.xml')
    Copy-Item -LiteralPath 'packaging/Assets' -Destination (Join-Path $stage 'Assets') -Recurse
    New-Item -ItemType Directory -Path (Join-Path $stage 'Public') -Force | Out-Null
    $kit = Get-ChildItem "${env:ProgramFiles(x86)}/Windows Kits/10/bin" -Directory | Where-Object { $_.Name -match '^10\.' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    $makeappx = Join-Path $kit.FullName 'x64/makeappx.exe'
    & $makeappx pack /d $stage /p (Join-Path $dist 'IvoryTasks-1.0.0-x64.msix') /o *> (Join-Path $dist 'makeappx.log')
    if ($LASTEXITCODE) { Get-Content -LiteralPath (Join-Path $dist 'makeappx.log') -Tail 25; throw 'MSIX packaging failed.' }
    Copy-Item -LiteralPath 'scripts/Install.ps1' -Destination $dist
    Copy-Item -LiteralPath 'README.md' -Destination $dist
    if (Test-Path -LiteralPath 'VALIDATION.md') { Copy-Item -LiteralPath 'VALIDATION.md' -Destination $dist }
    Copy-Item -LiteralPath 'scripts/Launch.ps1' -Destination $dist
    $stage | Set-Content -LiteralPath (Join-Path $dist 'staging-path.txt')
    Compress-Archive -Path (Join-Path $stage 'App') -DestinationPath (Join-Path $dist 'IvoryTasks-portable-x64.zip') -Force
    Get-FileHash -LiteralPath (Join-Path $dist 'IvoryTasks-1.0.0-x64.msix') -Algorithm SHA256 | Format-List
    Write-Host "Package created in $dist. Use Sign.ps1 with a code-signing certificate before distributing."
} finally { Pop-Location }
