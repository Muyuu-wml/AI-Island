$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$sandbox = Join-Path $root ('artifacts/install-test-' + [guid]::NewGuid().ToString('N'))
$config = Join-Path $sandbox '.config/opencode'
$plugin = Join-Path $config 'plugins/ai-island'
$installer = Join-Path $root 'scripts/Install-Hooks.ps1'
function Assert($condition, $message) { if (-not $condition) { throw $message } }
try {
    New-Item -ItemType Directory -Path (Join-Path $config 'plugins') -Force | Out-Null
    $unrelated = Join-Path $config 'opencode.jsonc'
    $original = '{ /* retain comments */ "plugins": ["other-plugin"], }'
    [IO.File]::WriteAllText($unrelated, $original)
    [IO.File]::WriteAllText((Join-Path $config 'plugins/other.js'), '// unrelated plugin')
    & $installer -Provider OpenCode -ConfigRoot $sandbox
    Assert (Test-Path -LiteralPath (Join-Path $plugin 'index.js')) 'Plugin entrypoint missing'
    $bridge = Get-Content -LiteralPath (Join-Path $plugin 'bridge.json') -Raw | ConvertFrom-Json
    Assert ($bridge.bridge -eq (Join-Path $root 'artifacts/hook/AIIsland.Hook.exe')) 'Wrong bridge path'
    & $installer -Provider OpenCode -ConfigRoot $sandbox
    Assert (@(Get-ChildItem -LiteralPath (Join-Path $config 'ai-island-backups')).Count -eq 1) 'Reinstall did not back up existing plugin'
    Assert (@(Get-ChildItem -LiteralPath (Join-Path $config 'plugins') -Directory).Count -eq 1) 'Backup would be auto-loaded as another plugin'
    Assert ((Get-Content -LiteralPath $unrelated -Raw) -eq $original) 'Unrelated JSONC changed'
    & $installer -Provider OpenCode -ConfigRoot $sandbox -Uninstall
    & $installer -Provider OpenCode -ConfigRoot $sandbox -Uninstall
    Assert (-not (Test-Path -LiteralPath $plugin)) 'Uninstall left the plugin installed'
    Assert (Test-Path -LiteralPath (Join-Path $config 'plugins/other.js')) 'Unrelated plugin removed'
    Write-Host 'OpenCode install, reinstall, backup and uninstall checks passed.'
} finally {
    if (Test-Path -LiteralPath $sandbox) { Remove-Item -LiteralPath $sandbox -Recurse -Force }
}
