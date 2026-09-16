$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $root ('artifacts/installer-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $fixture '.claude') -Force | Out-Null
$path = Join-Path $fixture '.claude/settings.json'
$original = '{"theme":"dark","hooks":{"Stop":[{"matcher":"*","hooks":[{"type":"command","command":"echo existing"}]}]}}'
[IO.File]::WriteAllText($path, $original)
& "$PSScriptRoot/Install-Hooks.ps1" -ConfigRoot $fixture
& "$PSScriptRoot/Install-Hooks.ps1" -ConfigRoot $fixture
$codexPath = Join-Path $fixture '.codex/hooks.json'
$codex = Get-Content -LiteralPath $codexPath -Raw | ConvertFrom-Json
if ($codex.hooks.UserPromptSubmit.Count -ne 1) { throw 'Codex hook duplicated' }
$command = $codex.hooks.UserPromptSubmit[0].hooks[0].command
# Execute the exact installed command, preserving quotes across the process boundary.
# Empty JSON exercises shell/host startup without injecting a fake AI session.
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
'{}' | & powershell -NoProfile -NonInteractive -EncodedCommand $encoded
if ($LASTEXITCODE -ne 0) { throw 'Installed Codex command failed in PowerShell' }
$config = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
if ($config.theme -ne 'dark' -or $config.hooks.Stop.Count -ne 2 -or $config.hooks.Stop[0].hooks[0].command -ne 'echo existing') { throw 'Installer lost existing config or duplicated itself' }
& "$PSScriptRoot/Install-Hooks.ps1" -ConfigRoot $fixture -Uninstall
$codex = Get-Content -LiteralPath $codexPath -Raw | ConvertFrom-Json
if ($codex.hooks.UserPromptSubmit.Count -ne 0) { throw 'Codex uninstall left its hook' }
$config = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
if ($config.hooks.Stop.Count -ne 1 -or $config.hooks.Stop[0].hooks[0].command -ne 'echo existing') { throw 'Uninstall removed unrelated hook' }
if (@(Get-ChildItem (Join-Path $fixture '.claude') -Filter '*.bak').Count -ne 3) { throw 'Missing backups' }
[IO.File]::WriteAllText($path, '{invalid')
$failed = $false
try { & "$PSScriptRoot/Install-Hooks.ps1" -ConfigRoot $fixture -Provider Claude } catch { $failed = $true }
if (-not $failed -or [IO.File]::ReadAllText($path) -ne '{invalid') { throw 'Malformed settings were overwritten' }
Write-Host 'PASS install / repeat / preserve existing / uninstall / backup / reject malformed settings'
