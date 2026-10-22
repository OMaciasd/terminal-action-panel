param(
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    throw 'Windows no proporcionó la carpeta LOCALAPPDATA del usuario actual.'
}

$installDirectory = Join-Path $env:LOCALAPPDATA 'TerminalActionPanel'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)) 'Terminal Action Panel.lnk'
$startMenuDirectory = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)) 'Terminal Action Panel'
$startMenuShortcut = Join-Path $startMenuDirectory 'Terminal Action Panel.lnk'
$executable = Join-Path $installDirectory 'TerminalActionPanel.exe'

if (Test-Path -LiteralPath $executable -PathType Leaf) {
    $running = @()
    try {
        $running = @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'TerminalActionPanel.exe'" -ErrorAction Stop |
            Where-Object { $_.ExecutablePath -and [string]::Equals($_.ExecutablePath, $executable, [StringComparison]::OrdinalIgnoreCase) })
    }
    catch {
        throw "No se pudo comprobar el estado de Terminal Action Panel. $($_.Exception.Message)"
    }

    if ($running.Count -gt 0 -and -not $Force) {
        $answer = Read-Host 'Hay una sesión abierta. Desinstalar terminará PowerShell y sus procesos alojados. ¿Continuar? (s/N)'
        if ($answer -notmatch '^(s|si|sí)$') {
            Write-Host 'Desinstalación cancelada.'
            return
        }
    }
    if ($running.Count -gt 0) {
        foreach ($process in $running) {
            Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
        }
    }
}

$ownedFiles = @(
    'TerminalActionPanel.exe', 'host-init.ps1', 'install.ps1', 'update.ps1',
    'uninstall.ps1', 'README.md', 'LICENSE', 'version.json'
)
foreach ($ownedFile in $ownedFiles) {
    $path = Join-Path $installDirectory $ownedFile
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        Remove-Item -LiteralPath $path -Force
    }
}

foreach ($shortcut in @($desktopShortcut, $startMenuShortcut)) {
    if (Test-Path -LiteralPath $shortcut -PathType Leaf) {
        Remove-Item -LiteralPath $shortcut -Force
    }
}
if ((Test-Path -LiteralPath $startMenuDirectory -PathType Container) -and
    @(Get-ChildItem -LiteralPath $startMenuDirectory -Force).Count -eq 0) {
    Remove-Item -LiteralPath $startMenuDirectory -Force
}

if ((Test-Path -LiteralPath $installDirectory -PathType Container) -and
    @(Get-ChildItem -LiteralPath $installDirectory -Force).Count -eq 0) {
    Remove-Item -LiteralPath $installDirectory -Force
}
elseif (Test-Path -LiteralPath $installDirectory -PathType Container) {
    Write-Host "Se conservaron archivos ajenos a la aplicación en $installDirectory."
}

Write-Host 'Terminal Action Panel se desinstaló. No se eliminó el historial ni archivos de PowerShell.'
