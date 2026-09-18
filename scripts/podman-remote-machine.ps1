<#
.SYNOPSIS
    Builds the Debian package and installs it in a container, to test the dashboard's remote
    path without a second computer.

.DESCRIPTION
    Observer's remote path - package, service, HTTPS, token, fingerprint, machine list - is
    the only part that no test can exercise on its own, because by definition it needs two
    machines. This script stands up the second one with podman.

    What this test proves: that the path works end to end, from the .deb to the row in the
    sidebar.

    What it does NOT prove: that the numbers are right on different hardware. The container
    shares the kernel of podman's virtual machine, so the CPU and memory you will see are
    the VM's, not those of a separate computer.

.PARAMETER WorkDirectory
    Where the built .deb ends up. Created if it does not exist.

.PARAMETER Name
    The container's name. Use the same name to pick up yesterday's test.

.PARAMETER Remove
    Removes the container and exits. Warning: the token and fingerprint disappear with it.

.EXAMPLE
    .\scripts\podman-remote-machine.ps1

.EXAMPLE
    .\scripts\podman-remote-machine.ps1 -Remove

.NOTES
    Three things measured on 2026-08-28, which explain why the script is built this way.

    1. The .deb HAS TO be built inside a container: Windows has no dpkg-deb and no strip,
       and the mounted repository shows up with mode 0777, so pack.sh's chmods would not
       hold. The repository is mounted read-only and copied inside with tar, so the build
       does not reuse the obj folders built on Windows.

    2. From the Windows host the container is reached at "localhost", NOT at "127.0.0.1",
       which refuses the connection. It works because Kestrel also listens on IPv6
       (https://[::]:5058) and podman's forwarder relays from [::1]. A process listening
       only on IPv4 would not be reachable from localhost in any way.
       Do NOT use the virtual machine's IP address: it changes on every WSL restart, and
       the dashboard would turn red one day for no visible reason.

    3. The service has to be started BY HAND after installation, and that is not a defect:
       inside the container /usr/sbin/policy-rc.d answers 101, and the maintainer scripts -
       which use deb-systemd-invoke and not systemctl precisely for this reason - respect it.
       Seeing that line in apt's output confirms that the rule works.
#>

[CmdletBinding()]
param(
    [string] $WorkDirectory = (Join-Path $env:TEMP 'observer-remote-machine'),
    # Stays Italian on purpose ('esterna' = external): a container left over from an earlier run
    # holds host port 5058, and both -Remove and the next run find it only by this name.
    [string] $Name = 'obs-esterna',
    [switch] $Remove
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

function Write-Step([string] $text) {
    Write-Host ''
    Write-Host "==> $text" -ForegroundColor Cyan
}

if ($Remove) {
    Write-Step "Removing the container $Name"
    podman rm -f $Name
    Write-Host "Done. That container's token and fingerprint no longer exist: if you run the" -ForegroundColor Yellow
    Write-Host "test again, machines.json has to be rewritten with the new values." -ForegroundColor Yellow
    return
}

Write-Step 'Checking that podman is up'
$podmanMachines = podman machine list --format '{{.Name}} {{.Running}}' 2>$null
if (-not $podmanMachines) { throw 'podman is not responding. Try: podman machine start' }
Write-Host $podmanMachines

if (-not (Test-Path $WorkDirectory)) {
    New-Item -ItemType Directory -Path $WorkDirectory | Out-Null
}

$version = ([xml](Get-Content (Join-Path $repoRoot 'Directory.Build.props'))).Project.PropertyGroup.Version
$package = "observer_${version}_amd64.deb"

Write-Step "Building $package inside a container (a few minutes)"

# The repository is mounted READ-ONLY and copied inside: that way the Linux build does not
# reuse the Windows obj folders, and nothing that happens here can dirty the working tree.
$buildScript = @'
set -e
apt-get update -qq > /dev/null
apt-get install -y -qq binutils > /dev/null
mkdir -p /work && cd /src
tar -cf - --exclude=./.git --exclude=bin --exclude=obj . | (cd /work && tar -xf -)
cd /work && bash packaging/linux/pack.sh
cp packaging/linux/out/*.deb /out/
'@

podman run --rm `
    -v "${repoRoot}:/src:ro" `
    -v "${WorkDirectory}:/out" `
    mcr.microsoft.com/dotnet/sdk:10.0 `
    bash -c $buildScript

$builtPackage = Join-Path $WorkDirectory $package
if (-not (Test-Path $builtPackage)) { throw "The package $package was not produced." }
Write-Host ("Built: {0} bytes" -f (Get-Item $builtPackage).Length)

Write-Step "Preparing the remote machine ($Name)"
podman rm -f $Name 2>$null | Out-Null
podman run -d --name $Name --systemd=always --hostname obs-container `
    -p 5058:5058 -v "${WorkDirectory}:/packages:ro" localhost/obs-systemd:latest | Out-Null

Write-Step 'Installing the package'
# The line "policy-rc.d returned 101" is expected here, and it is good news.
podman exec $Name bash -c "apt-get update -qq > /dev/null 2>&1; apt-get install -y -qq /packages/$package"

Write-Step 'Starting the service by hand'
podman exec $Name systemctl start observer.service
$state = podman exec $Name systemctl is-active observer.service
if ($state -ne 'active') { throw "The service did not start: $state" }

# Listening on IPv6 is what makes localhost reachable from the host: if that line were
# missing, the dashboard would never reach the container, and the error it showed would
# not name this cause.
$listening = podman exec $Name journalctl -u observer --no-pager -n 20 |
    Select-String 'Now listening on: https'
Write-Host $listening
if ($listening -notmatch '\[::\]') {
    Write-Warning 'The service is not listening on IPv6: "localhost" will not reach it from Windows.'
}

Write-Step 'Token and fingerprint'
$shareOutput = podman exec $Name observer share
$token = ($shareOutput | Select-String -Pattern '^\s{4}\S{20,}$').Matches.Value.Trim() | Select-Object -First 1
$fingerprint = ($shareOutput | Select-String -Pattern '([0-9A-F]{2}:){31}[0-9A-F]{2}').Matches.Value | Select-Object -First 1

if (-not $token -or -not $fingerprint) { throw 'Could not read the token or the fingerprint.' }

Write-Step 'Testing the transport from the host, before opening the dashboard'
# Without a token the expected answer is 401: proof that TLS works and that only the
# authorization is missing. A connection error here, on the other hand, is a network problem.
try {
    Invoke-WebRequest -Uri 'https://localhost:5058/metrics/latest' -SkipCertificateCheck -TimeoutSec 10 | Out-Null
    Write-Warning 'It answered without a token: unexpected.'
}
catch {
    $statusCode = $_.Exception.Response.StatusCode.value__
    if ($statusCode -eq 401) { Write-Host 'https://localhost:5058 answers 401 without a token: the transport works.' }
    else { throw "The container is not reachable from Windows: $($_.Exception.Message)" }
}

Write-Host ''
Write-Host 'Put this in %LOCALAPPDATA%\Observer\machines.json:' -ForegroundColor Green
Write-Host ''

[ordered]@{
    machines = @(
        [ordered]@{
            name        = 'obs-container'
            baseAddress = 'https://localhost:5058/'
            apiToken    = $token
            fingerprint = $fingerprint
        }
    )
} | ConvertTo-Json -Depth 4

Write-Host ''
Write-Host 'Then: dotnet run --project src\Observer.App' -ForegroundColor Green
Write-Host 'Expect two machines in the sidebar: this one and obs-container.'
Write-Host ''
Write-Host "To tear it down: .\scripts\podman-remote-machine.ps1 -Remove -Name $Name"
