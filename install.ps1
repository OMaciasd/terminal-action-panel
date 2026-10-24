param(
    [switch] $Update,
    [switch] $NoLaunch
)

$ErrorActionPreference = 'Stop'
$repository = 'OMaciasd/terminal-action-panel'
$apiUri = "https://api.github.com/repos/$repository/releases/latest"
$assetName = 'TerminalActionPanel-win-x64.zip'
$headers = @{
    'Accept' = 'application/vnd.github+json'
    'User-Agent' = 'TerminalActionPanel-installer'
    'X-GitHub-Api-Version' = '2022-11-28'
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Terminal Action Panel solo se instala en Windows.'
}
if (-not [Environment]::Is64BitOperatingSystem) {
    throw 'Terminal Action Panel requiere Windows de 64 bits.'
}
if ([Environment]::OSVersion.Version.Build -lt 17763) {
    throw 'Se requiere Windows 10 version 1809 (compilacion 17763) o posterior para ConPTY.'
}
if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    throw 'Windows no proporciono la carpeta LOCALAPPDATA del usuario actual.'
}

[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('TerminalActionPanel-' + [Guid]::NewGuid().ToString('N'))
$installDirectory = Join-Path $env:LOCALAPPDATA 'TerminalActionPanel'
$desktopDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
$startMenuDirectory = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)) 'Terminal Action Panel'
$installedExecutable = Join-Path $installDirectory 'TerminalActionPanel.exe'

function Get-InstalledAppProcess {
    if (-not (Test-Path -LiteralPath $installedExecutable -PathType Leaf)) {
        return @()
    }
    try {
        return @(Get-CimInstance -ClassName Win32_Process -Filter "Name = 'TerminalActionPanel.exe'" -ErrorAction Stop |
            Where-Object { $_.ExecutablePath -and [string]::Equals($_.ExecutablePath, $installedExecutable, [StringComparison]::OrdinalIgnoreCase) })
    }
    catch {
        throw "No se pudo comprobar si la aplicacion esta abierta; cierra Terminal Action Panel antes de continuar. $($_.Exception.Message)"
    }
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

function Assert-FilesReplaceable {
    param([Parameter(Mandatory = $true)][string[]] $Files)
    foreach ($name in $Files) {
        $path = Join-Path $installDirectory $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            continue
        }
        $unlocked = $false
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            try {
                $stream = [System.IO.File]::Open(
                    $path,
                    [System.IO.FileMode]::Open,
                    [System.IO.FileAccess]::ReadWrite,
                    [System.IO.FileShare]::None)
                $stream.Dispose()
                $unlocked = $true
                break
            }
            catch {
                Start-Sleep -Milliseconds 250
            }
        }
        if (-not $unlocked) {
            throw "No se puede reemplazar $path porque esta en uso. Cierra Terminal Action Panel, espera a que el antivirus termine y vuelve a intentarlo."
        }
    }
}

function Copy-OwnedFile {
    param(
        [Parameter(Mandatory = $true)][string] $Source,
        [Parameter(Mandatory = $true)][string] $Destination
    )
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        try {
            Copy-Item -LiteralPath $Source -Destination $Destination -Force -ErrorAction Stop
            return
        }
        catch {
            if ($attempt -eq 19) {
                throw "No se pudo copiar $Source a $Destination. $($_.Exception.Message)"
            }
            Start-Sleep -Milliseconds 250
        }
    }
}

function Set-ApplicationShortcut {
    param(
        [Parameter(Mandatory = $true)][string] $ShortcutPath,
        [Parameter(Mandatory = $true)][string] $TargetPath,
        [Parameter(Mandatory = $true)][string] $WorkingDirectory
    )
    $parent = Split-Path -Parent $ShortcutPath
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $TargetPath
    $shortcut.WorkingDirectory = $WorkingDirectory
    $shortcut.Description = 'Abrir Terminal Action Panel con una sesion real de PowerShell.'
    $shortcut.IconLocation = "$TargetPath,0"
    $shortcut.Save()
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shortcut)
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
}

try {
    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
    $archivePath = Join-Path $temporaryRoot $assetName
    $expandedDirectory = Join-Path $temporaryRoot 'package'

    Write-Host "Consultando la ultima version publicada de $repository ..."
    $release = Invoke-RestMethod -Uri $apiUri -Headers $headers -Method Get -UseBasicParsing
    if ($release.draft -or $release.prerelease) {
        throw 'La API de GitHub no devolvio una version estable publicada.'
    }
    if ($release.tag_name -notmatch '^v(?<version>[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?)$') {
        throw "La etiqueta publicada tiene un formato de version no reconocido: $($release.tag_name)"
    }
    $releaseVersion = $Matches.version

    $asset = @($release.assets | Where-Object { $_.name -eq $assetName }) | Select-Object -First 1
    if ($null -eq $asset) {
        throw "La publicacion $($release.tag_name) no contiene $assetName."
    }
    $digestMatch = [regex]::Match([string]$asset.digest, '^sha256:(?<hash>[0-9a-fA-F]{64})$')
    if (-not $digestMatch.Success) {
        throw 'GitHub no proporciono un digest SHA-256 verificable para el paquete; no se instalara.'
    }
    if ($asset.browser_download_url -notmatch '^https://github\.com/OMaciasd/terminal-action-panel/releases/download/') {
        throw 'La URL del paquete no pertenece a la publicacion oficial esperada.'
    }

    Invoke-WebRequest -Uri $asset.browser_download_url -Headers $headers -OutFile $archivePath -UseBasicParsing
    $actualHash = Get-Sha256Hex -Path $archivePath
    $expectedHash = $digestMatch.Groups['hash'].Value.ToLowerInvariant()
    if (-not [string]::Equals($actualHash, $expectedHash, [StringComparison]::Ordinal)) {
        throw "La verificacion SHA-256 fallo. Esperado: $expectedHash; recibido: $actualHash."
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $expectedFiles = @(
            'TerminalActionPanel.exe', 'host-init.ps1', 'install.ps1', 'update.ps1',
            'uninstall.ps1', 'README.md', 'LICENSE', 'version.json'
        )
        $entryNames = @($zip.Entries | ForEach-Object { $_.FullName })
        foreach ($entryName in $entryNames) {
            if ($entryName -notin $expectedFiles) {
                throw "El paquete contiene una ruta o archivo inesperado: $entryName"
            }
        }
        foreach ($expectedFile in $expectedFiles) {
            if ($entryNames -notcontains $expectedFile) {
                throw "El paquete no contiene el archivo requerido: $expectedFile"
            }
        }
    }
    finally {
        $zip.Dispose()
    }

    [System.IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $expandedDirectory)
    $packageVersion = Get-Content -LiteralPath (Join-Path $expandedDirectory 'version.json') -Raw | ConvertFrom-Json
    if ($packageVersion.version -ne $releaseVersion -or $packageVersion.releaseTag -ne $release.tag_name) {
        throw 'La version del contenido del paquete no coincide con la publicacion consultada.'
    }

    $runningProcesses = Get-InstalledAppProcess
    if ($runningProcesses.Count -gt 0) {
        throw 'Cierra Terminal Action Panel antes de instalar o actualizar, para no reemplazar una aplicacion en uso.'
    }

    if (-not (Test-Path -LiteralPath $installDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
    }
    $ownedFiles = @(
        'TerminalActionPanel.exe', 'host-init.ps1', 'install.ps1', 'update.ps1',
        'uninstall.ps1', 'README.md', 'LICENSE', 'version.json'
    )
    Assert-FilesReplaceable -Files $ownedFiles
    foreach ($ownedFile in $ownedFiles) {
        Copy-OwnedFile -Source (Join-Path $expandedDirectory $ownedFile) `
            -Destination (Join-Path $installDirectory $ownedFile)
    }

    Set-ApplicationShortcut -ShortcutPath (Join-Path $desktopDirectory 'Terminal Action Panel.lnk') `
        -TargetPath $installedExecutable -WorkingDirectory $installDirectory
    Set-ApplicationShortcut -ShortcutPath (Join-Path $startMenuDirectory 'Terminal Action Panel.lnk') `
        -TargetPath $installedExecutable -WorkingDirectory $installDirectory

    $mode = if ($Update) { 'actualizada' } else { 'instalada' }
    Write-Host "Terminal Action Panel $($release.tag_name) $mode en $installDirectory."
    Write-Host "SHA-256 verificado: $actualHash"
    Write-Host 'Se creo acceso directo en el escritorio y en el menu Inicio.'

    if (-not $NoLaunch -and -not $Update) {
        Start-Process -FilePath $installedExecutable -WorkingDirectory $installDirectory
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot -PathType Container) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
