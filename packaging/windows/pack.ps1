<#
.SYNOPSIS
    Builds the Observer MSI.

.DESCRIPTION
    Publishes the three executables into a payload folder and builds the package.
    Needs no elevation: building an MSI is an ordinary operation, installing one is not.

    The WiX project stays DELIBERATELY outside Observer.slnx: WixToolset.Sdk ships native
    binaries for Windows only and ICE validation always runs, so inside the solution it
    would fail the CI job "build (ubuntu-latest)" for ever, and that job is a required
    check of the ruleset.

.PARAMETER Configuration
    Release or Debug.

.EXAMPLE
    .\packaging\windows\pack.ps1
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$payload = Join-Path $PSScriptRoot 'payload'

if (Test-Path $payload) {
    Remove-Item $payload -Recurse -Force
}

foreach ($project in 'Observer.Service', 'Observer.App', 'Observer.Cli') {
    Write-Host "Publishing $project..."

    # Targeted at win-x64: without it, SkiaSharp ships the native libraries for EVERY platform
    # and the package grows from about ten megabytes to over a hundred.
    # And --self-contained true, measured on a real machine. With "false" the MSI installs
    # binaries that require ASP.NET Core 10 and does not check that it is there: on a PC with
    # .NET 8 the service starts, does not find the runtime, dies without a word, the service
    # manager waits thirty seconds and reports a timeout, and Windows Installer turns all of
    # it into "insufficient privileges". Three messages, and not one names the cause. On Linux
    # this does not happen, because the .deb declares aspnetcore-runtime-10.0 and apt refuses
    # to install without it; on Windows nothing resolves a dependency, so the package carries it.
    # Measured cost: payload 242 MB, MSI from 12.8 to 51 MB after compression. The trade-off
    # to know about: runtime security fixes no longer come from Windows Update, they come
    # with an Observer release.
    #
    # The comments are HERE and not between the arguments: a comment inside a backtick
    # continuation breaks it, and PowerShell reads the next line as a new command -
    # "The term '-c' is not recognized". It happened while writing this very comment, and the
    # syntax check does not see it: it is a run-time error, not a parse error.
    & dotnet publish (Join-Path $repoRoot "src\$project") `
        -c $Configuration -r win-x64 --self-contained true `
        -o $payload --nologo | Out-Null

    if ($LASTEXITCODE -ne 0) {
        throw "Publishing $project failed."
    }
}

# appsettings.Local.json is the file where a developer keeps their own token, and "dotnet
# publish" takes it along. It must be removed from the payload BEFORE packaging: an MSI
# ends up on GitHub Releases. The WiX project also has a guard that fails the build if it
# finds it, but finding it here is the normal case, not an anomaly to report.
Get-ChildItem $payload -Filter 'appsettings*.Local.json' -ErrorAction SilentlyContinue |
    ForEach-Object {
        Write-Host "Removing from the payload: $($_.Name)"
        Remove-Item $_.FullName -Force
    }

Write-Host 'Building the package...'
& dotnet build (Join-Path $PSScriptRoot 'Observer.wixproj') -c $Configuration --nologo

if ($LASTEXITCODE -ne 0) {
    throw 'Building the package failed.'
}

$msi = Get-ChildItem (Join-Path $PSScriptRoot 'bin') -Recurse -Filter 'Observer.msi' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

Write-Host ''
Write-Host ("Package: {0} ({1:N1} MB)" -f $msi.FullName, ($msi.Length / 1MB))
Write-Host ''
Write-Host 'Installing it needs an ELEVATED terminal:'
Write-Host ("    msiexec /i `"{0}`"" -f $msi.FullName)
Write-Host ''
Write-Host 'If this machine already has an Observer service registered by hand with'
Write-Host 'scripts\windows-service.ps1, uninstall it FIRST: the package does not know about it'
Write-Host 'and does not manage it.'
Write-Host ''
Write-Host 'Also check that the installation folder is EMPTY. Binaries copied by hand'
Write-Host 'come from a publish with no runtime identifier, and carry a'
Write-Host 'System.ServiceProcess.ServiceController.dll that does not work on Windows: with an'
Write-Host 'equal version number Windows Installer does NOT replace it, the service does not'
Write-Host "start, and the installation stops with error 1920, which talks about insufficient"
Write-Host 'privileges and does not name the real cause.'