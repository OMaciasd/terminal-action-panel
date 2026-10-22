param(
    [string] $Version = '1.0.0',
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot 'dist'
}
if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$') {
    throw "La versión '$Version' no tiene el formato SemVer esperado."
}
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'La compilación requiere Windows y el compilador de .NET Framework incluido con Windows.'
}

$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw "No se encontró el compilador de .NET Framework: $compiler"
}
$source = Join-Path $PSScriptRoot 'src\TerminalActionPanel.cs'
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
    throw "No se encontró el código fuente: $source"
}
if (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
}

$icon = Join-Path $PSScriptRoot 'src\app.ico'
$manifest = Join-Path $PSScriptRoot 'src\app.manifest'
if (-not (Test-Path -LiteralPath $icon -PathType Leaf)) {
    throw "No se encontró el icono: $icon"
}
if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) {
    throw "No se encontró el manifiesto: $manifest"
}

$numericVersion = (($Version -split '-')[0].Split('.') + @('0', '0', '0') | Select-Object -First 3) -join '.'
$assemblyVersion = "$numericVersion.0"
$assemblyInfo = @"
using System.Reflection;
[assembly: AssemblyTitle("Terminal Action Panel")]
[assembly: AssemblyDescription("Panel de productividad para una sesión real de PowerShell con ConPTY.")]
[assembly: AssemblyCompany("Oscar Macias")]
[assembly: AssemblyProduct("Terminal Action Panel")]
[assembly: AssemblyCopyright("MIT License")]
[assembly: AssemblyVersion("$assemblyVersion")]
[assembly: AssemblyFileVersion("$assemblyVersion")]
[assembly: AssemblyInformationalVersion("$Version")]
"@
$assemblyInfoPath = Join-Path ([System.IO.Path]::GetTempPath()) ('TerminalActionPanel-AssemblyInfo-' + [Guid]::NewGuid().ToString('N') + '.cs')
$utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($assemblyInfoPath, $assemblyInfo, $utf8WithoutBom)

$executable = Join-Path $OutputDirectory 'TerminalActionPanel.exe'
$compilerArguments = @(
    '/nologo',
    '/target:winexe',
    '/platform:x64',
    '/optimize+',
    '/debug-',
    '/warn:4',
    '/reference:System.Windows.Forms.dll',
    '/reference:System.Drawing.dll',
    "/win32icon:$icon",
    "/win32manifest:$manifest",
    "/out:$executable",
    $assemblyInfoPath,
    $source
)
try {
    & $compiler @compilerArguments
    if ($LASTEXITCODE -ne 0) {
        throw "La compilación de Terminal Action Panel falló con código $LASTEXITCODE."
    }
}
finally {
    Remove-Item -LiteralPath $assemblyInfoPath -Force -ErrorAction SilentlyContinue
}

$packageFiles = @(
    'host-init.ps1',
    'install.ps1',
    'update.ps1',
    'uninstall.ps1',
    'README.md',
    'LICENSE'
)
foreach ($file in $packageFiles) {
    $sourcePath = Join-Path $PSScriptRoot $file
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Falta un archivo requerido para el paquete: $sourcePath"
    }
    Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $OutputDirectory $file) -Force
}

$versionDocument = [ordered]@{
    version = $Version
    releaseTag = "v$Version"
    repository = 'https://github.com/OMaciasd/terminal-action-panel'
    target = 'Windows x64; .NET Framework incluido en Windows; PowerShell 5.1; ConPTY'
}
$versionPath = Join-Path $OutputDirectory 'version.json'
$versionJson = $versionDocument | ConvertTo-Json -Depth 3
$utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($versionPath, $versionJson, $utf8WithoutBom)

Write-Host "Compilación lista: $executable"
Write-Host "Versión del paquete: $Version"
