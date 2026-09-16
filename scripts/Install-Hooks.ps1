param(
    [ValidateSet('Codex','Claude','Both')][string]$Provider = 'Both',
    [string]$ConfigRoot,
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$bridge = Join-Path $root 'artifacts/hook/AIIsland.Hook.exe'
if (-not $Uninstall -and -not (Test-Path -LiteralPath $bridge)) { throw 'Run scripts/build.ps1 first.' }
$providers = if ($Provider -eq 'Both') { @('Codex','Claude') } else { @($Provider) }
foreach ($name in $providers) {
    $folder = if ($ConfigRoot) { Join-Path $ConfigRoot ('.' + $name.ToLower()) } elseif ($name -eq 'Codex' -and $env:CODEX_HOME) { $env:CODEX_HOME } elseif ($name -eq 'Claude' -and $env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $env:USERPROFILE ('.' + $name.ToLower()) }
    $path = Join-Path $folder $(if ($name -eq 'Codex') { 'hooks.json' } else { 'settings.json' })
    $config = if (Test-Path -LiteralPath $path) { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
    if (-not $config.PSObject.Properties['hooks']) { $config | Add-Member hooks ([pscustomobject]@{}) }
    $events = @('SessionStart','UserPromptSubmit','PreToolUse','PostToolUse','PermissionRequest','Stop','SessionEnd')
    if ($name -eq 'Codex') { $events += 'Interrupt' } else { $events += @('PostToolUseFailure','StopFailure') }
    foreach ($event in $events) {
        $entries = @()
        if ($config.hooks.PSObject.Properties[$event]) {
            foreach ($entry in @($config.hooks.$event)) {
                $remaining = @($entry.hooks | Where-Object { $_.command -notmatch 'AIIsland\.Hook\.exe"?\s+(Codex|Claude)$' })
                if ($remaining.Count -gt 0) { $entry.hooks = $remaining; $entries += $entry }
            }
        }
        if (-not $Uninstall) {
            $command = '"' + $bridge.Replace('\','/') + '" ' + $name
            # Codex on Windows evaluates commands in PowerShell: a quoted path
            # needs the call operator. Claude uses its existing shell syntax.
            if ($name -eq 'Codex') { $command = '& ' + $command }
            $entries += [pscustomobject]@{ hooks = @([pscustomobject]@{ type = 'command'; command = $command; timeout = 3 }) }
        }
        $config.hooks | Add-Member -NotePropertyName $event -NotePropertyValue @($entries) -Force
    }
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination ($path + '.ai-island-' + [guid]::NewGuid().ToString('N') + '.bak') }
    $json = $config | ConvertTo-Json -Depth 100
    [System.IO.File]::WriteAllText($path + '.ai-island.tmp', $json, [System.Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath ($path + '.ai-island.tmp') -Destination $path -Force
    Write-Host "$name hooks updated: $path"
}
Write-Host 'Restart CLI sessions. Review and trust Codex hooks in its /hooks interface if requested.'
