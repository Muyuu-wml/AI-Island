param(
    [string]$Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dotnet = if (Test-Path (Join-Path $root '.dotnet/dotnet.exe')) { Join-Path $root '.dotnet/dotnet.exe' } else { 'dotnet' }
$out = Join-Path $root 'artifacts/package'

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out -Force | Out-Null

& $dotnet publish (Join-Path $root 'src/AIIsland/AIIsland.csproj') -c Release -r $Runtime `
    --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $out
if ($LASTEXITCODE -ne 0) { throw 'App package failed.' }

# The hook stays a separate executable: CLIs invoke it repeatedly and it must start fast.
& $dotnet publish (Join-Path $root 'src/AIIsland.Hook/AIIsland.Hook.csproj') -c Release -r $Runtime `
    --self-contained true -p:PublishSingleFile=true -o $out
if ($LASTEXITCODE -ne 0) { throw 'Hook package failed.' }

Write-Host "Package: $out"
