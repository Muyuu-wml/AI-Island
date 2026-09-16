$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $root '.dotnet/dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
& $dotnet build (Join-Path $root 'AIIsland.sln') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
& $dotnet run --project (Join-Path $root 'AIIsland.Tests') -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
& $dotnet run --project (Join-Path $root 'AIIsland.SmokeTests') -c Release --no-build -- $root --balance-checks
if ($LASTEXITCODE -ne 0) { throw 'Balance checks failed' }
& $dotnet run --project (Join-Path $root 'AIIsland.SmokeTests') -c Release --no-build -- $root --monitor-order-checks
if ($LASTEXITCODE -ne 0) { throw 'Monitor order checks failed' }
& $dotnet run --project (Join-Path $root 'AIIsland.SmokeTests') -c Release --no-build -- $root --resources-checks
if ($LASTEXITCODE -ne 0) { throw 'System resource checks failed' }
& $dotnet run --project (Join-Path $root 'AIIsland.SmokeTests') -c Release --no-build -- $root --music-checks
if ($LASTEXITCODE -ne 0) { throw 'Music checks failed' }
& $dotnet run --project (Join-Path $root 'AIIsland.SmokeTests') -c Release --no-build -- $root --ui-checks
if ($LASTEXITCODE -ne 0) { throw 'Notification checks failed' }
& $dotnet run --project (Join-Path $root 'AIIsland.SmokeTests') -c Release --no-build -- $root --completion-checks
if ($LASTEXITCODE -ne 0) { throw 'Completion checks failed' }
& $dotnet publish (Join-Path $root 'AIIsland/AIIsland.csproj') -c Release --no-restore -o (Join-Path $root 'artifacts/app')
if ($LASTEXITCODE -ne 0) { throw 'App publish failed' }
& $dotnet publish (Join-Path $root 'AIIsland.Hook/AIIsland.Hook.csproj') -c Release --no-restore -o (Join-Path $root 'artifacts/hook')
if ($LASTEXITCODE -ne 0) { throw 'Hook publish failed' }
& "$PSScriptRoot/Test-HookInstaller.ps1"
