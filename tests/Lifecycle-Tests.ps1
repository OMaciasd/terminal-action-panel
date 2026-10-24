param(
    [string] $RepositoryRoot = (Join-Path $PSScriptRoot '..'),
    [int] $TimeoutSeconds = 90,
    [switch] $SkipReinstall
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Las pruebas de ciclo de vida solo se ejecutan en Windows.'
}
if (-not [Environment]::Is64BitOperatingSystem) {
    throw 'Las pruebas de ciclo de vida requieren Windows de 64 bits.'
}
if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    throw 'Windows no proporcionó LOCALAPPDATA.'
}

$repository = [System.IO.Path]::GetFullPath($RepositoryRoot)
$installDirectory = Join-Path $env:LOCALAPPDATA 'TerminalActionPanel'
$desktopDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
$startMenuDirectory = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)) 'Terminal Action Panel'
$desktopShortcut = Join-Path $desktopDirectory 'Terminal Action Panel.lnk'
$startMenuShortcut = Join-Path $startMenuDirectory 'Terminal Action Panel.lnk'
$executable = Join-Path $installDirectory 'TerminalActionPanel.exe'
$installedInstaller = Join-Path $installDirectory 'install.ps1'
$installedUpdater = Join-Path $installDirectory 'update.ps1'
$installedUninstaller = Join-Path $installDirectory 'uninstall.ps1'
$foreignFile = Join-Path $installDirectory 'foreign-sentinel.txt'
$ownedFiles = @(
    'TerminalActionPanel.exe', 'host-init.ps1', 'install.ps1', 'update.ps1',
    'uninstall.ps1', 'README.md', 'LICENSE', 'version.json'
)

function Assert-Condition {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) {
        throw "FAIL: $Message"
    }
    Write-Host "PASS: $Message"
}

function Get-AppProcesses {
    try {
        return @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'TerminalActionPanel.exe'" -ErrorAction Stop |
            Where-Object {
                $_.ExecutablePath -and
                [string]::Equals($_.ExecutablePath, $executable, [StringComparison]::OrdinalIgnoreCase)
            })
    }
    catch {
        throw "No se pudo consultar el proceso de la aplicación: $($_.Exception.Message)"
    }
}

function Stop-App {
    $processes = @(Get-AppProcesses)
    foreach ($process in $processes) {
        $managed = Get-Process -Id $process.ProcessId -ErrorAction SilentlyContinue
        if ($null -ne $managed) {
            [void]$managed.CloseMainWindow()
            if (-not $managed.WaitForExit(10000)) {
                Stop-Process -Id $managed.Id -Force -ErrorAction SilentlyContinue
                $managed.WaitForExit(5000) | Out-Null
            }
        }
    }
    if (@(Get-AppProcesses).Count -gt 0) {
        throw 'La aplicación sigue abierta después de intentar cerrarla.'
    }
}

function Invoke-Installer {
    param([string] $InstallerPath, [switch] $Update)
    if (-not (Test-Path -LiteralPath $InstallerPath -PathType Leaf)) {
        throw "No se encontró el instalador: $InstallerPath"
    }
    if ($Update) {
        & $InstallerPath -Update -NoLaunch
    }
    else {
        & $InstallerPath -NoLaunch
    }
}

function Get-LatestReleaseTag {
    $headers = @{
        'Accept' = 'application/vnd.github+json'
        'User-Agent' = 'TerminalActionPanel-lifecycle-test'
        'X-GitHub-Api-Version' = '2022-11-28'
    }
    $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/OMaciasd/terminal-action-panel/releases/latest' -Headers $headers -UseBasicParsing
    if ($release.draft -or $release.prerelease) {
        throw 'GitHub no devolvió una versión estable publicada.'
    }
    return [string]$release.tag_name
}

function Assert-Installed {
    param([string] $ExpectedTag)
    Assert-Condition (Test-Path -LiteralPath $installDirectory -PathType Container) 'el directorio de instalación existe'
    Assert-Condition (Test-Path -LiteralPath $executable -PathType Leaf) 'el ejecutable instalado existe'
    foreach ($name in $ownedFiles) {
        Assert-Condition (Test-Path -LiteralPath (Join-Path $installDirectory $name) -PathType Leaf) "el archivo $name está instalado"
    }
    $version = Get-Content -LiteralPath (Join-Path $installDirectory 'version.json') -Raw | ConvertFrom-Json
    Assert-Condition ($version.releaseTag -eq $ExpectedTag) "version.json coincide con la publicación $ExpectedTag"
    Assert-Condition ([string]::Equals($version.repository, 'https://github.com/OMaciasd/terminal-action-panel', [StringComparison]::Ordinal)) 'version.json apunta al repositorio esperado'
}

function Assert-Shortcuts {
    $shell = New-Object -ComObject WScript.Shell
    try {
        foreach ($shortcutPath in @($desktopShortcut, $startMenuShortcut)) {
            Assert-Condition (Test-Path -LiteralPath $shortcutPath -PathType Leaf) "existe el acceso directo $shortcutPath"
            $shortcut = $shell.CreateShortcut($shortcutPath)
            try {
                Assert-Condition ([string]::Equals($shortcut.TargetPath, $executable, [StringComparison]::OrdinalIgnoreCase)) "el acceso directo apunta a $executable"
                Assert-Condition ([string]::Equals($shortcut.WorkingDirectory, $installDirectory, [StringComparison]::OrdinalIgnoreCase)) 'el acceso directo usa el directorio de instalación'
            }
            finally {
                [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shortcut)
            }
        }
    }
    finally {
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
    }
    Assert-Condition (@(Get-ChildItem -LiteralPath $desktopDirectory -Filter 'Terminal Action Panel*.lnk' -File).Count -eq 1) 'el escritorio contiene un único acceso directo de la aplicación'
    Assert-Condition (@(Get-ChildItem -LiteralPath $startMenuDirectory -Filter 'Terminal Action Panel*.lnk' -File).Count -eq 1) 'el menú Inicio contiene un único acceso directo de la aplicación'
}

function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][string] $Path)
    $stream = [System.IO.File]::OpenRead($Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hashBytes = $sha.ComputeHash($stream)
        return ([System.BitConverter]::ToString($hashBytes) -replace '-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
        $stream.Dispose()
    }
}

function Get-OwnedFileHashes {
    $hashes = [ordered]@{}
    foreach ($name in $ownedFiles) {
        $path = Join-Path $installDirectory $name
        $hashes[$name] = Get-Sha256Hex -Path $path
    }
    return $hashes
}

function Assert-HashSetsEqual {
    param($Before, $After, [string] $Description)
    foreach ($name in $ownedFiles) {
        Assert-Condition ($Before[$name] -eq $After[$name]) "${Description}: $name no cambió"
    }
}

function Start-ShortcutAndVerify {
    Start-Process -FilePath $desktopShortcut
    $process = $null
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $candidate = @(Get-AppProcesses) | Select-Object -First 1
        if ($null -ne $candidate) {
            $process = Get-Process -Id $candidate.ProcessId -ErrorAction SilentlyContinue
            if ($null -ne $process -and $process.MainWindowHandle -ne [IntPtr]::Zero) {
                break
            }
        }
        Start-Sleep -Milliseconds 200
    }
    if ($null -eq $process -or $process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'FAIL: el acceso directo no abrió una ventana de Terminal Action Panel.'
    }
    Write-Host "PASS: el acceso directo abrió la ventana (PID $($process.Id))."
    if (-not $process.CloseMainWindow()) {
        throw 'No se pudo solicitar el cierre de la ventana abierta por el acceso directo.'
    }
    if (-not $process.WaitForExit(15000)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        throw 'La ventana abierta por el acceso directo no se cerró.'
    }
    Assert-Condition ($process.HasExited) 'la ventana abierta por el acceso directo se cerró'
}

Stop-App
$expectedTag = Get-LatestReleaseTag
Write-Host "Publicación estable esperada: $expectedTag"

Write-Host '--- Instalación inicial ---'
Invoke-Installer -InstallerPath (Join-Path $repository 'install.ps1')
Assert-Installed -ExpectedTag $expectedTag
Assert-Shortcuts
$firstHashes = Get-OwnedFileHashes

Write-Host '--- Instalación repetida (idempotencia) ---'
Invoke-Installer -InstallerPath (Join-Path $repository 'install.ps1')
Assert-Installed -ExpectedTag $expectedTag
Assert-Shortcuts
$secondHashes = Get-OwnedFileHashes
Assert-HashSetsEqual -Before $firstHashes -After $secondHashes -Description 'instalación repetida'

Write-Host '--- Apertura mediante acceso directo ---'
Start-ShortcutAndVerify

Write-Host '--- Actualización desde la versión publicada ---'
Invoke-Installer -InstallerPath $installedUpdater -Update
Assert-Installed -ExpectedTag $expectedTag
Assert-Shortcuts
$updatedHashes = Get-OwnedFileHashes
Assert-HashSetsEqual -Before $secondHashes -After $updatedHashes -Description 'actualización a la misma versión'

Write-Host '--- Desinstalación conservando archivos ajenos ---'
Set-Content -LiteralPath $foreignFile -Value 'sentinel' -Encoding ASCII
& $installedUninstaller -Force
foreach ($name in $ownedFiles) {
    Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $installDirectory $name))) "el archivo propio $name fue eliminado"
}
Assert-Condition (-not (Test-Path -LiteralPath $desktopShortcut)) 'el acceso directo del escritorio fue eliminado'
Assert-Condition (-not (Test-Path -LiteralPath $startMenuShortcut)) 'el acceso directo del menú Inicio fue eliminado'
Assert-Condition (Test-Path -LiteralPath $foreignFile -PathType Leaf) 'el archivo ajeno se conservó'
Remove-Item -LiteralPath $foreignFile -Force

Write-Host '--- Reinstalación para probar la desinstalación limpia ---'
Invoke-Installer -InstallerPath (Join-Path $repository 'install.ps1')
Assert-Installed -ExpectedTag $expectedTag
Assert-Shortcuts

Write-Host '--- Desinstalación limpia ---'
& $installedUninstaller -Force
Assert-Condition (-not (Test-Path -LiteralPath $installDirectory)) 'el directorio de instalación se eliminó al quedar vacío'
Assert-Condition (-not (Test-Path -LiteralPath $startMenuDirectory)) 'el directorio del menú Inicio se eliminó al quedar vacío'

if (-not $SkipReinstall) {
    Write-Host '--- Reinstalación final ---'
    Invoke-Installer -InstallerPath (Join-Path $repository 'install.ps1')
    Assert-Installed -ExpectedTag $expectedTag
    Assert-Shortcuts
}

Write-Host 'PASS: todas las pruebas de instalación, actualización y desinstalación finalizaron correctamente.'
