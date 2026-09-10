param(
    [Parameter(Mandatory = $true)]
    [string]$SetupPath,

    [Parameter(Mandatory = $true)]
    [string]$ReleaseVersion,

    [int]$StartupTimeoutSeconds = 20,

    [int]$StabilitySeconds = 6
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $SetupPath)) {
    throw "Installer was not found at $SetupPath."
}
if ($ReleaseVersion -notmatch '^v[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "Invalid release version '$ReleaseVersion'."
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$installDir = Join-Path $env:RUNNER_TEMP "serverdesk-installed-$([Guid]::NewGuid().ToString('N'))"
$desktopDir = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
$programsDir = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
$desktopShortcut = Join-Path $desktopDir "ServerDesk.lnk"
$startMenuShortcut = Join-Path $programsDir "ServerDesk.lnk"
$appExe = Join-Path $installDir "ServerDesk.App.exe"
$brandingIcon = Join-Path $installDir "Assets\Branding\serverdesk.ico"
$uninstaller = Join-Path $installDir "unins000.exe"
$uninstallRegistryRoot = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall"

function Invoke-CheckedProcess {
    param(
        [Parameter(Mandatory = $true)] [string]$FilePath,
        [Parameter(Mandatory = $true)] [string[]]$ArgumentList,
        [Parameter(Mandatory = $true)] [string]$Description
    )

    Write-Host "==> $Description"
    $process = Start-Process -FilePath $FilePath -ArgumentList $ArgumentList -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "$Description failed with exit code $($process.ExitCode)."
    }
}

function Get-ServerDeskUninstallEntry {
    if (-not (Test-Path $uninstallRegistryRoot)) {
        return $null
    }

    return Get-ChildItem $uninstallRegistryRoot -ErrorAction SilentlyContinue |
        ForEach-Object { Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue } |
        Where-Object { $_.DisplayName -eq "ServerDesk" } |
        Select-Object -First 1
}

function Assert-Shortcut {
    param(
        [Parameter(Mandatory = $true)] [string]$Path,
        [Parameter(Mandatory = $true)] [string]$ExpectedTarget,
        [Parameter(Mandatory = $true)] [string]$ExpectedIcon
    )

    if (-not (Test-Path $Path)) {
        throw "Expected shortcut was not created: $Path"
    }

    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($Path)
        $actualTarget = [IO.Path]::GetFullPath($shortcut.TargetPath)
        $expectedTargetPath = [IO.Path]::GetFullPath($ExpectedTarget)
        if (-not $actualTarget.Equals($expectedTargetPath, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Shortcut $Path targets '$actualTarget' instead of '$expectedTargetPath'."
        }

        $iconPath = ($shortcut.IconLocation -split ',')[0].Trim().Trim('"')
        if ([string]::IsNullOrWhiteSpace($iconPath)) {
            throw "Shortcut $Path does not declare an icon source."
        }
        $actualIcon = [IO.Path]::GetFullPath($iconPath)
        $expectedIconPath = [IO.Path]::GetFullPath($ExpectedIcon)
        if (-not $actualIcon.Equals($expectedIconPath, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Shortcut $Path uses icon '$actualIcon' instead of '$expectedIconPath'."
        }
    }
    finally {
        if ($null -ne $shell) {
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
        }
    }
}

try {
    # /TASKS forces the desktop task on even when a previous installer preference exists on the runner.
    Invoke-CheckedProcess -FilePath (Resolve-Path $SetupPath).Path -Description "Install ServerDesk silently" -ArgumentList @(
        "/VERYSILENT",
        "/SUPPRESSMSGBOXES",
        "/NORESTART",
        "/SP-",
        "/DIR=$installDir",
        "/TASKS=desktopicon"
    )

    if (-not (Test-Path $appExe)) {
        throw "Installed ServerDesk executable is missing: $appExe"
    }
    if (-not (Test-Path $brandingIcon)) {
        throw "Installed ServerDesk branding icon is missing: $brandingIcon"
    }
    if (-not (Test-Path $uninstaller)) {
        throw "ServerDesk uninstaller is missing: $uninstaller"
    }

    Assert-Shortcut -Path $desktopShortcut -ExpectedTarget $appExe -ExpectedIcon $brandingIcon
    Assert-Shortcut -Path $startMenuShortcut -ExpectedTarget $appExe -ExpectedIcon $brandingIcon

    $uninstallEntry = Get-ServerDeskUninstallEntry
    if ($null -eq $uninstallEntry) {
        throw "ServerDesk was not registered in the current user's Windows uninstall registry."
    }
    if ([string]::IsNullOrWhiteSpace($uninstallEntry.UninstallString)) {
        throw "ServerDesk uninstall registry entry has no UninstallString."
    }
    if ([string]::IsNullOrWhiteSpace($uninstallEntry.DisplayIcon)) {
        throw "ServerDesk uninstall registry entry has no DisplayIcon."
    }
    $actualDisplayIcon = [IO.Path]::GetFullPath(($uninstallEntry.DisplayIcon -split ',')[0].Trim().Trim('"'))
    $expectedDisplayIcon = [IO.Path]::GetFullPath($brandingIcon)
    if (-not $actualDisplayIcon.Equals($expectedDisplayIcon, [StringComparison]::OrdinalIgnoreCase)) {
        throw "ServerDesk uninstall DisplayIcon '$actualDisplayIcon' does not use '$expectedDisplayIcon'."
    }

    Write-Host "Desktop shortcut, Start Menu shortcut, installed branding icon and uninstall registration verified."

    & (Join-Path $repoRoot "scripts\test-windows-gui-launch.ps1") `
        -OutputDirectory $installDir `
        -StartupTimeoutSeconds $StartupTimeoutSeconds `
        -StabilitySeconds $StabilitySeconds

    Invoke-CheckedProcess -FilePath $uninstaller -Description "Uninstall ServerDesk silently" -ArgumentList @(
        "/VERYSILENT",
        "/SUPPRESSMSGBOXES",
        "/NORESTART"
    )

    $cleanupDeadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $cleanupDeadline -and ((Test-Path $appExe) -or (Test-Path $desktopShortcut) -or (Test-Path $startMenuShortcut) -or $null -ne (Get-ServerDeskUninstallEntry))) {
        Start-Sleep -Milliseconds 250
    }

    if (Test-Path $appExe) {
        throw "Uninstall left the ServerDesk executable behind at $appExe"
    }
    if (Test-Path $desktopShortcut) {
        throw "Uninstall left the Desktop shortcut behind at $desktopShortcut"
    }
    if (Test-Path $startMenuShortcut) {
        throw "Uninstall left the Start Menu shortcut behind at $startMenuShortcut"
    }
    if ($null -ne (Get-ServerDeskUninstallEntry)) {
        throw "Uninstall left the ServerDesk Apps/Uninstall registry entry behind."
    }

    Write-Host "ServerDesk installer smoke passed: install, branding icon, shortcuts, GUI launch, uninstall and cleanup are verified."
}
finally {
    try {
        Get-Process ServerDesk.App -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    }
    catch {
    }

    if (Test-Path $uninstaller) {
        try {
            Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -Wait -ErrorAction SilentlyContinue | Out-Null
        }
        catch {
        }
    }

    foreach ($shortcut in @($desktopShortcut, $startMenuShortcut)) {
        Remove-Item $shortcut -Force -ErrorAction SilentlyContinue
    }
}
