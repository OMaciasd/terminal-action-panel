param(
    [string] $ExecutablePath = (Join-Path $PSScriptRoot '..\dist\TerminalActionPanel.exe')
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$resolvedExecutable = [System.IO.Path]::GetFullPath($ExecutablePath)
if (-not (Test-Path -LiteralPath $resolvedExecutable -PathType Leaf)) {
    throw "No se encontró la aplicación compilada: $resolvedExecutable. Ejecuta build.ps1 primero."
}

foreach ($scriptName in @('build.ps1', 'install.ps1', 'update.ps1', 'uninstall.ps1', 'host-init.ps1')) {
    $scriptPath = Join-Path $PSScriptRoot ("..\{0}" -f $scriptName)
    $tokens = $null
    $parseErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) {
        throw "Error de sintaxis en ${scriptName}: $($parseErrors[0].Message)"
    }
}
Write-Host 'PASS: sintaxis de los scripts de instalación y ciclo de vida.'

Write-Host "Ejecutando pruebas automatizadas con $resolvedExecutable …"
$process = Start-Process -FilePath $resolvedExecutable -ArgumentList '--self-test' -Wait -PassThru
$logPath = Join-Path ([System.IO.Path]::GetTempPath()) ("TerminalActionPanel-self-test-{0}.log" -f $process.Id)
if (Test-Path -LiteralPath $logPath -PathType Leaf) {
    Get-Content -LiteralPath $logPath -Encoding UTF8 | ForEach-Object { Write-Host $_ }
    Remove-Item -LiteralPath $logPath -Force
}
if ($process.ExitCode -ne 0) {
    throw "La suite de pruebas terminó con código $($process.ExitCode)."
}
