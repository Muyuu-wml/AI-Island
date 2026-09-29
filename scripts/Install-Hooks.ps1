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
    if ($name -eq 'Codex') { $events += @('Interrupt','SubagentStart','SubagentStop') } else { $events += @('PostToolUseFailure','StopFailure') }
    foreach ($event in $events) {
        $entries = @()
        if ($config.hooks.PSObject.Properties[$event]) {
            foreach ($entry in @($config.hooks.$event)) {
                $remaining = @($entry.hooks | Where-Object { $_.command -notmatch 'AIIsland\.Hook\.exe("|)?\s+(Codex|Claude)$' })
                if ($remaining.Count -gt 0) { $entry.hooks = $remaining; $entries += $entry }
            }
        }
        if (-not $Uninstall) {
            $command = '"' + $bridge.Replace('\','/') + '" ' + $name
            $hook = [pscustomobject]@{ type = 'command'; command = $(if ($name -eq 'Codex') { 'cmd.exe /d /c ' + [IO.Path]::GetFullPath((Join-Path (Split-Path $bridge -Parent) 'AIIsland.Hook-Codex.cmd')).Replace('/','\') } else { $command }); timeout = 3 }
            if ($name -eq 'Codex') {
                # Codex runs generic commands through a Windows shell. A quote-free .cmd
                # entry avoids cmd.exe's outer-quote parsing and keeps stdin attached.
                $windowsBridge = [IO.Path]::GetFullPath($bridge).Replace('/','\')
                $wrapper = Join-Path (Split-Path $bridge -Parent) 'AIIsland.Hook-Codex.cmd'
                $wrapperText = "@echo off`r`n`"$windowsBridge`" Codex`r`n"
                [IO.File]::WriteAllText($wrapper, $wrapperText, [Text.UTF8Encoding]::new($false))
                if ($wrapper -notmatch '\s') {
                    $hook | Add-Member -NotePropertyName commandWindows -NotePropertyValue ('cmd.exe /d /c ' + [IO.Path]::GetFullPath($wrapper).Replace('/','\'))
                } else {
                    # Paths with spaces require the PowerShell fallback; the normal project
                    # layout is quote-free and uses the safer wrapper above.
                    $hook | Add-Member -NotePropertyName commandWindows -NotePropertyValue ('powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -Command "& ''' + $windowsBridge.Replace("'", "''") + ''' Codex"')
                }
            }
            if ($event -notin @('SessionEnd','Interrupt')) { $hook | Add-Member -NotePropertyName async -NotePropertyValue $true }
            $entries += [pscustomobject]@{ hooks = @($hook) }
        }
        $config.hooks | Add-Member -NotePropertyName $event -NotePropertyValue @($entries) -Force
    }
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination ($path + '.ai-island-' + [guid]::NewGuid().ToString('N') + '.bak') }
    $json = $config | ConvertTo-Json -Depth 100
    [IO.File]::WriteAllText($path + '.ai-island.tmp', $json, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath ($path + '.ai-island.tmp') -Destination $path -Force
    Write-Host "$name hooks updated: $path"
}
Write-Host 'Restart all Codex sessions (including app-server) and review/trust the changed hooks in /hooks.'
