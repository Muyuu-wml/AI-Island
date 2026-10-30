param(
    [switch]$SkipPublish
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dotnet = if (Test-Path (Join-Path $root '.dotnet/dotnet.exe')) { Join-Path $root '.dotnet/dotnet.exe' } else { 'dotnet' }

& $dotnet build (Join-Path $root 'AIIsland.sln') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
if ($SkipPublish) { return }

$app = Join-Path $root 'artifacts/app'
$hook = Join-Path $root 'artifacts/hook'
foreach ($folder in @($app, $hook)) {
    if (Test-Path $folder) { Remove-Item $folder -Recurse -Force }
}

& $dotnet publish (Join-Path $root 'src/AIIsland/AIIsland.csproj') -c Release -o $app
if ($LASTEXITCODE -ne 0) { throw 'App publish failed.' }
& $dotnet publish (Join-Path $root 'src/AIIsland.Hook/AIIsland.Hook.csproj') -c Release -o $hook
if ($LASTEXITCODE -ne 0) { throw 'Hook publish failed.' }

# The hook bridge must sit next to the island executable.
Copy-Item (Join-Path $hook '*') $app -Force
Write-Host "App : $app"
Write-Host "Hook: $app/AIIsland.Hook.exe"
Write-Host 'Run artifacts/app/AIIsland.exe, then install hooks from Settings.'
