param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [int]$StartupTimeoutSeconds = 20,

    [int]$StabilitySeconds = 6
)

$ErrorActionPreference = 'Stop'

$exePath = Join-Path $OutputDirectory 'ServerDesk.App.exe'
if (-not (Test-Path $exePath)) {
    throw "ServerDesk.App.exe was not found at $exePath"
}

$startedAt = Get-Date
$process = Start-Process -FilePath $exePath -WorkingDirectory $OutputDirectory -PassThru

function Get-RecentServerDeskEvents {
    try {
        Get-WinEvent -FilterHashtable @{
            LogName = 'Application'
            StartTime = $startedAt.AddSeconds(-3)
        } -ErrorAction SilentlyContinue |
            Where-Object {
                $_.ProviderName -in @('.NET Runtime', 'Application Error', 'Windows Error Reporting') -or
                $_.Message -match 'ServerDesk'
            } |
            Select-Object -First 12 TimeCreated, ProviderName, Id, LevelDisplayName, Message |
            Format-List |
            Out-String
    }
    catch {
        "Unable to read Application event log: $($_.Exception.GetType().FullName)"
    }
}

try {
    $deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
    $windowSeen = $false

    while ((Get-Date) -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) {
            $events = Get-RecentServerDeskEvents
            throw "ServerDesk exited during GUI startup with code $($process.ExitCode).`n$events"
        }

        if ($process.MainWindowHandle -ne 0 -and $process.MainWindowTitle -eq 'ServerDesk') {
            $windowSeen = $true
            break
        }

        Start-Sleep -Milliseconds 250
    }

    if (-not $windowSeen) {
        $events = Get-RecentServerDeskEvents
        throw "ServerDesk remained alive but did not expose the expected visible 'ServerDesk' main window within $StartupTimeoutSeconds second(s). Current title: '$($process.MainWindowTitle)'.`n$events"
    }

    Write-Host "Visible ServerDesk main window detected (handle $($process.MainWindowHandle))."
    Start-Sleep -Seconds $StabilitySeconds

    $process.Refresh()
    if ($process.HasExited) {
        $events = Get-RecentServerDeskEvents
        throw "ServerDesk exited during the $StabilitySeconds-second GUI stability window with code $($process.ExitCode).`n$events"
    }

    if ($process.MainWindowHandle -eq 0 -or $process.MainWindowTitle -ne 'ServerDesk') {
        $events = Get-RecentServerDeskEvents
        throw "ServerDesk lost its expected main window during the GUI stability window. Current title: '$($process.MainWindowTitle)'.`n$events"
    }

    Write-Host "ServerDesk GUI launch smoke passed: process is alive and the expected main window remained visible."
}
finally {
    try {
        $process.Refresh()
        if (-not $process.HasExited) {
            $null = $process.CloseMainWindow()
            if (-not $process.WaitForExit(5000)) {
                $process.Kill($true)
                $process.WaitForExit()
            }
        }
    }
    catch {
        try {
            if (-not $process.HasExited) {
                $process.Kill($true)
            }
        }
        catch {
        }
    }
    finally {
        $process.Dispose()
    }
}
