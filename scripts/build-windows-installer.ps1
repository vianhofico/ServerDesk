param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [string]$ReleaseVersion = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$publishDir = (Resolve-Path $PublishDirectory).Path
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$outputDir = (Resolve-Path $OutputDirectory).Path

if ([string]::IsNullOrWhiteSpace($ReleaseVersion)) {
    $ReleaseVersion = (Get-Content (Join-Path $repoRoot "RELEASE_VERSION") -Raw).Trim()
}

if ($ReleaseVersion -notmatch '^v([0-9]+\.[0-9]+\.[0-9]+)$') {
    throw "Installer requires a stable vMAJOR.MINOR.PATCH release version; observed '$ReleaseVersion'."
}

$appVersion = $Matches[1]
$installerScript = Join-Path $repoRoot "installer\ServerDesk.iss"
$brandingIcon = Join-Path $repoRoot "src\ServerDesk.App\Assets\Branding\serverdesk.ico"
$appExe = Join-Path $publishDir "ServerDesk.App.exe"

foreach ($requiredPath in @($installerScript, $brandingIcon, $appExe)) {
    if (-not (Test-Path $requiredPath)) {
        throw "Installer input is missing: $requiredPath"
    }
}

$isccCandidates = @()
$command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if ($null -ne $command) {
    $isccCandidates += $command.Source
}
if (${env:ProgramFiles(x86)}) {
    $isccCandidates += (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe")
}
if ($env:ProgramFiles) {
    $isccCandidates += (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
}

$iscc = $isccCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup 6 compiler (ISCC.exe) was not found. Install Inno Setup 6 before building the installer."
}

Write-Host "==> Build ServerDesk Windows installer with $iscc"
& $iscc `
    "/DAppVersion=$appVersion" `
    "/DReleaseTag=$ReleaseVersion" `
    "/DSourceDir=$publishDir" `
    "/DOutputDir=$outputDir" `
    "/DBrandingIcon=$brandingIcon" `
    $installerScript

if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
}

$expectedName = "ServerDesk-$ReleaseVersion-win-x64-setup.exe"
$installerPath = Join-Path $outputDir $expectedName
if (-not (Test-Path $installerPath)) {
    throw "Expected installer was not produced at $installerPath."
}

$installerLength = (Get-Item $installerPath).Length
if ($installerLength -lt 1MB) {
    throw "Installer output is unexpectedly small: $installerLength bytes."
}

Write-Host "Windows installer verified: $installerPath ($installerLength bytes)"
