# Shared launcher. Use -DryRun to inspect arguments without connecting to a car.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('cooling', 'fuel', 'intake')]
    [string]$Task,
    [ValidateRange(0.1, 5.0)]
    [double]$RateHz = 1,
    [ValidateRange(0.0, 2147483647.0)]
    [double]$DurationSec = 0,
    [string]$OutPath,
    [ValidateNotNullOrEmpty()]
    [string]$Ecu = 'MEVD174K',
    [string]$ExePath,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$configPath = Join-Path $repoRoot "config\sample-$Task.txt"
if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
    throw "Missing capture configuration: $configPath"
}
if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $repoRoot 'src\ObdLogger\bin\Release\net48\ObdLogger.exe'
}
if ([string]::IsNullOrWhiteSpace($OutPath)) {
    $OutPath = Join-Path $repoRoot "logs\$Task"
}
$ExePath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ExePath)
$OutPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutPath)

# OutPath is always a directory, including when it does not exist yet.
$outputDirectory = $OutPath.TrimEnd([char[]]@('\', '/')) + [System.IO.Path]::DirectorySeparatorChar
$culture = [System.Globalization.CultureInfo]::InvariantCulture
$loggerArgs = @('log', '--config', $configPath, '--rate', $RateHz.ToString($culture),
    '--ecu', $Ecu, '--out', $outputDirectory)
if ($DurationSec -gt 0) {
    $loggerArgs += @('--duration', $DurationSec.ToString($culture))
}

if ($DryRun) {
    [pscustomobject]@{
        Task = $Task
        Executable = $ExePath
        ConfigPath = $configPath
        Arguments = $loggerArgs
    }
    return
}
if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
    throw "Logger executable not found: $ExePath. Run build.ps1 with a .NET SDK installed, or supply -ExePath."
}
[void][System.IO.Directory]::CreateDirectory($OutPath)
Write-Host "Capture task: $Task | Config: $configPath | Output: $outputDirectory"
& $ExePath @loggerArgs
if ($LASTEXITCODE -ne 0) {
    throw "ObdLogger exited with code $LASTEXITCODE."
}
