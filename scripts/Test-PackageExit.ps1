param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$resolvedExecutable = (Resolve-Path -LiteralPath $Executable).Path
$process = Start-Process -FilePath $resolvedExecutable -ArgumentList '--shutdown-check' -WindowStyle Hidden -PassThru
try {
    if (-not $process.WaitForExit(20000)) {
        $process.Kill()
        throw 'Packaged shutdown check timed out (possible exception dialog).'
    }
    if ($process.ExitCode -ne 0) { throw "Packaged shutdown check failed: exit code $($process.ExitCode)" }
    Write-Host 'PASS Published single-file EXE loads WPF controls and exits through the tray shutdown handler.'
}
finally { $process.Dispose() }
