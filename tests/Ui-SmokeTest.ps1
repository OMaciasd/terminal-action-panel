param(
    [string] $ExecutablePath = (Join-Path $PSScriptRoot '..\dist\TerminalActionPanel.exe'),
    [int] $TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class TapTestNative {
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
'@

$exe = [System.IO.Path]::GetFullPath($ExecutablePath)
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "No se encontró la aplicación: $exe"
}
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne [Threading.ApartmentState]::STA) {
    throw 'Ejecuta esta prueba con powershell.exe -STA.'
}

$originalClipboard = $null
$hadClipboardText = $false
try {
    $hadClipboardText = [Windows.Forms.Clipboard]::ContainsText([Windows.Forms.TextDataFormat]::UnicodeText)
    if ($hadClipboardText) {
        $originalClipboard = [Windows.Forms.Clipboard]::GetText([Windows.Forms.TextDataFormat]::UnicodeText)
    }
}
catch {
    $hadClipboardText = $false
}

function Get-ClipboardText {
    for ($attempt = 0; $attempt -lt 25; $attempt++) {
        try {
            if (-not [Windows.Forms.Clipboard]::ContainsText([Windows.Forms.TextDataFormat]::UnicodeText)) {
                return $null
            }
            return [Windows.Forms.Clipboard]::GetText([Windows.Forms.TextDataFormat]::UnicodeText)
        }
        catch [System.Runtime.InteropServices.ExternalException] {
            Start-Sleep -Milliseconds 100
        }
    }
    throw 'El portapapeles permaneció bloqueado por otro proceso.'
}

function Set-ClipboardText {
    param([string] $Text)
    for ($attempt = 0; $attempt -lt 25; $attempt++) {
        try {
            [Windows.Forms.Clipboard]::SetText($Text, [Windows.Forms.TextDataFormat]::UnicodeText)
            return
        }
        catch [System.Runtime.InteropServices.ExternalException] {
            Start-Sleep -Milliseconds 100
        }
    }
    throw 'No se pudo escribir en el portapapeles de prueba.'
}

function Get-AllNames {
    param([Windows.Automation.AutomationElement] $Window)
    $elements = $Window.FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.Condition]::TrueCondition)
    return @($elements | ForEach-Object { $_.Current.Name } | Where-Object { $_ })
}

function Wait-Until {
    param(
        [scriptblock] $Predicate,
        [string] $Description,
        [int] $Timeout = $TimeoutSeconds
    )
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.Elapsed.TotalSeconds -lt $Timeout) {
        if (& $Predicate) {
            return
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Tiempo de espera agotado: $Description"
}

function Find-NamedElement {
    param(
        [Windows.Automation.AutomationElement] $Window,
        [string] $Name
    )
    $condition = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::NameProperty, $Name)
    $element = $Window.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $element) {
        throw "No se encontró el control '$Name'."
    }
    return $element
}

function Invoke-Control {
    param([Windows.Automation.AutomationElement] $Element)
    try {
        $pattern = $Element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)
        $pattern.Invoke()
        return
    }
    catch {
        $handle = [IntPtr]$Element.Current.NativeWindowHandle
        if ($handle -eq [IntPtr]::Zero) {
            throw "El control no admite InvokePattern ni tiene una ventana nativa."
        }
        [void][TapTestNative]::SendMessage($handle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
    }
}

function Wait-ForStatus {
    param(
        [Windows.Automation.AutomationElement] $Window,
        [string] $Text,
        [int] $Timeout = $TimeoutSeconds
    )
    Wait-Until -Description "estado que contenga '$Text'" -Timeout $Timeout -Predicate {
        @(Get-AllNames $Window | Where-Object { $_ -like "*$Text*" }).Count -gt 0
    }
}

function Wait-Clipboard {
    param(
        [scriptblock] $ClipboardPredicate,
        [string] $Description,
        [int] $Timeout = $TimeoutSeconds
    )
    Wait-Until -Description $Description -Timeout $Timeout -Predicate {
        $value = Get-ClipboardText
        if ($null -eq $value) { return $false }
        & $ClipboardPredicate $value
    }
}

$process = $null
try {
    $process = Start-Process -FilePath $exe -PassThru
    $pidCondition = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
    $window = $null
    Wait-Until -Description 'ventana principal' -Predicate {
        $script:window = [Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [Windows.Automation.TreeScope]::Children, $pidCondition)
        $null -ne $script:window
    }
    $window = $script:window

    $targets = Get-AllNames $window
    if (-not ($targets | Where-Object { $_ -like '*PowerShell 5.1*' })) {
        throw 'No se muestra la terminal objetivo PowerShell 5.1.'
    }
    if (-not ($targets | Where-Object { $_ -like '*token efectivo*' })) {
        throw 'No se muestra el usuario y el token efectivo.'
    }
    Write-Host 'PASS: ventana, terminal objetivo y permisos visibles.'

    $paste = Find-NamedElement -Window $window -Name 'PASTE + EXECUTE'
    $copy = Find-NamedElement -Window $window -Name 'COPY ALL'
    $clear = Find-NamedElement -Window $window -Name 'CLEAR ALL'

    Wait-Until -Description 'PowerShell listo para el primer comando' -Predicate {
        $paste.Current.IsEnabled
    }
    Write-Host 'PASS: la sesión ConPTY está lista sin ejecutar nada.'

    Set-ClipboardText "Start-Sleep -Milliseconds 1500`r`nWrite-Output 'TAP_UI_OK'"
    Invoke-Control $paste
    Wait-Until -Description 'botón PASTE + EXECUTE deshabilitado durante la ejecución' -Timeout 5 -Predicate {
        -not $paste.Current.IsEnabled
    }
    Wait-ForStatus -Window $window -Text 'Ejecutando' -Timeout 5
    Write-Host 'PASS: ejecución en curso y botón bloqueado para evitar duplicados.'

    Wait-Until -Description 'PowerShell de nuevo disponible' -Predicate {
        $paste.Current.IsEnabled
    }
    Wait-ForStatus -Window $window -Text 'Lista'
    Write-Host 'PASS: comando multilínea del portapapeles completado.'

    $interrupt = Find-NamedElement -Window $window -Name 'INTERRUMPIR'
    Set-ClipboardText 'Start-Sleep -Seconds 30'
    Invoke-Control $paste
    Wait-Until -Description 'proceso largo en ejecución' -Predicate {
        $interrupt.Current.IsEnabled
    }
    Start-Sleep -Seconds 2
    Invoke-Control $interrupt
    Wait-Until -Description 'PowerShell recuperado tras Ctrl+C' -Predicate {
        $paste.Current.IsEnabled
    }
    Write-Host 'PASS: INTERRUMPIR detiene un proceso activo y devuelve la sesión.'

    Invoke-Control $copy
    Wait-Clipboard -Description 'resultado de COPY ALL en el portapapeles' -ClipboardPredicate {
        param($value)
        $value.Contains('TAP_UI_OK')
    }
    Write-Host 'PASS: COPY ALL incluye la salida del comando ejecutado.'

    Set-ClipboardText '1..120 | ForEach-Object { "TAP_LINE_$_" }'
    Invoke-Control $paste
    Wait-Until -Description 'PowerShell disponible tras la salida extensa' -Predicate {
        $paste.Current.IsEnabled
    }
    Invoke-Control $copy
    Wait-Clipboard -Description 'historial extenso en el portapapeles' -ClipboardPredicate {
        param($value)
        $value.Contains('TAP_LINE_1') -and $value.Contains('TAP_LINE_120') -and $value.Contains('TAP_UI_OK')
    }
    Write-Host 'PASS: COPY ALL conserva la primera y la última línea del scrollback, no solo la salida visible.'

    Invoke-Control $clear
    Wait-ForStatus -Window $window -Text 'Transcripción y scrollback de la aplicación eliminados'
    Set-ClipboardText 'TAP_CLIPBOARD_SENTINEL'
    Invoke-Control $copy
    Wait-ForStatus -Window $window -Text 'No hay salida disponible para copiar'
    if ((Get-ClipboardText) -ne 'TAP_CLIPBOARD_SENTINEL') {
        throw 'CLEAR ALL no vació la transcripción que COPY ALL utiliza.'
    }
    Write-Host 'PASS: CLEAR ALL vacía la pantalla y el historial temporal de la aplicación.'

    Set-ClipboardText '   '
    Invoke-Control $paste
    Wait-ForStatus -Window $window -Text 'portapapeles está vacío'
    if (-not $paste.Current.IsEnabled) {
        throw 'El botón PASTE + EXECUTE no volvió a habilitarse tras un portapapeles vacío.'
    }
    Write-Host 'PASS: portapapeles vacío manejado sin ejecutar nada.'

    Set-ClipboardText "Write-Output 'TAP_AFTER_CLEAR'"
    Invoke-Control $paste
    Wait-Until -Description 'nuevo comando tras CLEAR ALL' -Predicate {
        $paste.Current.IsEnabled
    }
    Invoke-Control $copy
    Wait-Clipboard -Description 'salida nueva después de CLEAR ALL' -ClipboardPredicate {
        param($value)
        $value.Contains('TAP_AFTER_CLEAR') -and -not $value.Contains('TAP_UI_OK')
    }
    Write-Host 'PASS: la sesión sigue operativa y el historial limpio no resucita.'

    Set-ClipboardText 'exit'
    Invoke-Control $paste
    Wait-ForStatus -Window $window -Text 'La terminal se cerró'
    if ($paste.Current.IsEnabled) {
        throw 'PASTE + EXECUTE sigue habilitado después de cerrarse la terminal.'
    }
    Write-Host 'PASS: terminal cerrada detectada y ejecución bloqueada.'

    if (-not $process.CloseMainWindow()) {
        throw 'No se pudo solicitar el cierre de la ventana principal.'
    }
    Wait-Until -Description 'cierre limpio de la aplicación' -Predicate { $process.HasExited }
    if ($process.ExitCode -ne 0) {
        throw "La aplicación terminó con código $($process.ExitCode)."
    }
    Write-Host 'PASS: cierre limpio de la ventana y de la sesión alojada.'
    Write-Host 'PASS: prueba de interfaz completa.'
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        try { $process.Kill() } catch {}
        $process.WaitForExit()
    }
    try {
        if ($hadClipboardText) {
            [Windows.Forms.Clipboard]::SetText($originalClipboard, [Windows.Forms.TextDataFormat]::UnicodeText)
        }
        else {
            [Windows.Forms.Clipboard]::Clear()
        }
    }
    catch {}
}
