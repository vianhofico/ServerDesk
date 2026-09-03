param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$project = "src/ServerDesk.App/ServerDesk.App.csproj"
$runtime = "win-x64"
$diagnosticsDirectory = "artifacts/release-publish"

New-Item -ItemType Directory -Force -Path $diagnosticsDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

function Invoke-DotNet {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    Write-Host "==> $Description"
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

# A normal solution restore is already proven by CI and warms the shared package graph.
# Package pruning is disabled only for the release publish path because .NET 10/NuGet
# has had RID-specific pruning/restore regressions; normal product builds keep defaults.
Invoke-DotNet -Description "Restore solution for release" -Arguments @(
    "restore",
    "ServerDesk.sln",
    "--disable-parallel",
    "-p:RestoreEnablePackagePruning=false",
    "-bl:$diagnosticsDirectory/solution-restore.binlog"
)

# Restore the exact runtime graph explicitly. This prevents `dotnet publish` from doing
# an opaque implicit RID restore and gives us a dedicated binary log on failure.
Invoke-DotNet -Description "Restore win-x64 runtime graph" -Arguments @(
    "restore",
    $project,
    "--runtime", $runtime,
    "--disable-parallel",
    "--verbosity", "normal",
    "-p:RestoreEnablePackagePruning=false",
    "-bl:$diagnosticsDirectory/rid-restore.binlog"
)

Invoke-DotNet -Description "Publish self-contained win-x64 package" -Arguments @(
    "publish",
    $project,
    "--configuration", $Configuration,
    "--runtime", $runtime,
    "--self-contained", "true",
    "--no-restore",
    "-p:RestoreEnablePackagePruning=false",
    "-p:PublishSingleFile=false",
    "-p:DebugType=None",
    "-bl:$diagnosticsDirectory/publish.binlog",
    "--output", $OutputDirectory
)

$exePath = Join-Path $OutputDirectory "ServerDesk.App.exe"
if (-not (Test-Path $exePath)) {
    throw "Published ServerDesk.App.exe was not produced at $exePath."
}

$terminalAssets = Join-Path $OutputDirectory "TerminalFrontend/dist"
if (-not (Test-Path $terminalAssets)) {
    throw "Published terminal frontend assets were not produced at $terminalAssets."
}

Write-Host "Self-contained win-x64 publish verified at $OutputDirectory"
