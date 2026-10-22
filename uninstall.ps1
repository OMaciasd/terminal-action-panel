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
$script:failedRemovals = $false

function Remove-PathWithRetry {
    param([Parameter(Mandatory = $true)][string] $Path)
    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        if (-not (Test-Path -LiteralPath $Path)) {
            return $true
        }
        try {
            Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
            return $true
        }
        catch {
            Start-Sleep -Milliseconds 400
        }
    }
    $script:failedRemovals = $true
    Write-Warning "No se pudo eliminar $Path. Cierra el Explorador de Windows o OneDrive y eliminalo manualmente."
    return $false
}

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
            Wait-Process -Id $process.ProcessId -Timeout 5 -ErrorAction SilentlyContinue
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
        [void](Remove-PathWithRetry -Path $path)
    }
}

foreach ($shortcut in @($desktopShortcut, $startMenuShortcut)) {
    if (Test-Path -LiteralPath $shortcut -PathType Leaf) {
        [void](Remove-PathWithRetry -Path $shortcut)
    }
}
if ((Test-Path -LiteralPath $startMenuDirectory -PathType Container) -and
    @(Get-ChildItem -LiteralPath $startMenuDirectory -Force).Count -eq 0) {
    [void](Remove-PathWithRetry -Path $startMenuDirectory)
}

if ((Test-Path -LiteralPath $installDirectory -PathType Container) -and
    @(Get-ChildItem -LiteralPath $installDirectory -Force).Count -eq 0) {
    [void](Remove-PathWithRetry -Path $installDirectory)
}
elseif (Test-Path -LiteralPath $installDirectory -PathType Container) {
    Write-Host "Se conservaron archivos ajenos a la aplicación en $installDirectory."
}

if ($script:failedRemovals) {
    Write-Warning 'La desinstalación terminó con elementos pendientes. Revisa los avisos anteriores.'
}
else {
    Write-Host 'Terminal Action Panel se desinstaló. No se eliminó el historial ni archivos de PowerShell.'
}
