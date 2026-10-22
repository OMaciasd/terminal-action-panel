param(
    [switch] $NoLaunch
)

$ErrorActionPreference = 'Stop'
$installer = Join-Path $PSScriptRoot 'install.ps1'
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
    throw 'No se encontró install.ps1. Vuelve a instalar desde una versión publicada del repositorio.'
}

& $installer -Update -NoLaunch:$NoLaunch
