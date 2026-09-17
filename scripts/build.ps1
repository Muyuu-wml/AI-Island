$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $root '.dotnet/dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
& $dotnet build (Join-Path $root 'AIIsland.sln') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
& $dotnet publish (Join-Path $root 'AIIsland/AIIsland.csproj') -c Release --no-restore -o (Join-Path $root 'artifacts/app')
if ($LASTEXITCODE -ne 0) { throw 'App publish failed' }
& $dotnet publish (Join-Path $root 'AIIsland.Hook/AIIsland.Hook.csproj') -c Release --no-restore -o (Join-Path $root 'artifacts/hook')
if ($LASTEXITCODE -ne 0) { throw 'Hook publish failed' }
