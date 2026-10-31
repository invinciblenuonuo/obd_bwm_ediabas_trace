[CmdletBinding()]
param(
    [ValidateRange(0.1, 5.0)][double]$RateHz = 1,
    [ValidateRange(0.0, 2147483647.0)][double]$DurationSec = 0,
    [string]$OutPath,
    [ValidateNotNullOrEmpty()][string]$Ecu = 'MEVD174K',
    [string]$ExePath,
    [switch]$DryRun
)
& (Join-Path $PSScriptRoot 'Start-Capture.ps1') -Task fuel @PSBoundParameters
