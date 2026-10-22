param(
    [Parameter(Mandatory = $true)]
    [string] $Marker
)

try {
    # Keep commands from this hosted session out of the user's persistent history file.
    Import-Module PSReadLine -ErrorAction Stop
    Set-PSReadLineOption -HistorySaveStyle SaveNothing -ErrorAction Stop
}
catch {
    # PowerShell without PSReadLine has no PSReadLine history file to save.
}

$global:__TerminalActionPanelPromptMarker = $Marker
function global:prompt {
    [Console]::Out.Write("`r`n$global:__TerminalActionPanelPromptMarker`r`n")
    return "PS $($executionContext.SessionState.Path.CurrentLocation)> "
}
