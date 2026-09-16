param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$target = Join-Path $root 'AIIsland.exe'
$output = Join-Path $root 'artifacts/package/app'
function Assert-PackageNotRunning {
    $protectedPaths = @($target, (Join-Path $output 'AIIsland.exe'), (Join-Path $root 'artifacts/app/AIIsland.exe'))
    $running = @(Get-Process -Name AIIsland -ErrorAction SilentlyContinue | Where-Object {
        $protectedPaths -contains $_.Path
    })
    if ($running.Count -gt 0) {
        throw 'AI Island is running. Exit it before publishing. Never rename or replace a running single-file EXE: delayed assembly loads may read the replacement bundle.'
    }
}
Assert-PackageNotRunning
$dotnet = Join-Path $root '.dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = 'dotnet' }
if (-not $SkipTests) { & "$PSScriptRoot/build.ps1" }
$cache = Join-Path $root 'artifacts/nuget'
$restoreArgs = @('--configfile', (Join-Path $root 'NuGet.Config'))
if (Get-ChildItem -LiteralPath $cache -Filter '*.nupkg' -ErrorAction SilentlyContinue) {
    $restoreArgs += @('--source', $cache, '--source', 'https://api.nuget.org/v3/index.json')
}
& $dotnet restore (Join-Path $root 'AIIsland/AIIsland.csproj') -r win-x64 -p:SelfContained=true -p:PublishSingleFile=true @restoreArgs
if ($LASTEXITCODE -ne 0) { throw 'Package restore failed' }
Assert-PackageNotRunning
& $dotnet publish (Join-Path $root 'AIIsland/AIIsland.csproj') -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $output
if ($LASTEXITCODE -ne 0) { throw 'Single-file publish failed' }
& "$PSScriptRoot/Test-PackageExit.ps1" -Executable (Join-Path $output 'AIIsland.exe')
Assert-PackageNotRunning
Copy-Item -LiteralPath (Join-Path $output 'AIIsland.exe') -Destination $target -Force
Write-Host "Ready: $root/AIIsland.exe (Windows x64, self-contained)"
