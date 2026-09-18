<#
.SYNOPSIS
    Installs, uninstalls or queries Observer.Service as a Windows service.

.DESCRIPTION
    Requires an ELEVATED PowerShell: registering a service changes system settings.
    The script refuses to proceed without elevation instead of failing halfway through.

    It does not generate, ask for or know any token: since plan 3 the service generates and
    keeps its own on first start. If the published folder does contain an
    appsettings.Local.json - a token set by hand, which wins over the generated one - it copies
    it and RESTRICTS its permissions, because the destination folder is readable by anyone with
    an account, and a machine token must not be.

.PARAMETER Action
    Install, Uninstall, Status or Verify.

    Verify does NOT require elevation, and that is deliberate: the question it answers is
    whether the interactive user can open the pipe of a service running as LocalSystem.
    Asked from an elevated console, that question would always get the answer "yes" and
    would prove nothing.

.PARAMETER Source
    The folder produced by "dotnet publish -c Release -o artifacts/service".

.PARAMETER Destination
    Where to install. Default: C:\Program Files\Observer.

.EXAMPLE
    .\scripts\windows-service.ps1 -Action Install -Source .\artifacts\service

.EXAMPLE
    .\scripts\windows-service.ps1 -Action Uninstall
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Install', 'Uninstall', 'Status', 'Verify')]
    [string] $Action,

    [string] $Source = '.\artifacts\service',

    [string] $Destination = 'C:\Program Files\Observer'
)

$ErrorActionPreference = 'Stop'
$serviceName = 'Observer'

function Test-Elevation {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Show-Status {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue

    if (-not $service) {
        Write-Host "The service '$serviceName' is not registered."
        return
    }

    $wmi = Get-CimInstance -ClassName Win32_Service -Filter "Name='$serviceName'"
    Write-Host "Name        : $($service.Name)"
    Write-Host "Status      : $($service.Status)"
    Write-Host "Startup     : $($service.StartType)"
    Write-Host "Account     : $($wmi.StartName)"
    Write-Host "Executable  : $($wmi.PathName)"
}

if ($Action -eq 'Status') {
    Show-Status
    return
}

if ($Action -eq 'Verify') {
    Show-Status
    Write-Host ''

    $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    Write-Host "Checking as: $($currentIdentity.Name)"
    Write-Host "Elevated: $(Test-Elevation)  (an elevated check would prove nothing)"
    Write-Host ''

    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(
        '.', $serviceName, [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::None,
        [System.Security.Principal.TokenImpersonationLevel]::Identification)

    try {
        # "." and not "localhost": localhost would go through SMB, and the service would classify
        # the connection as coming from the network.
        $pipe.Connect(3000)
        Write-Host 'RESULT: the pipe opens. The DACL lets this user in.' -ForegroundColor Green
        Write-Host 'On this channel the token is NOT needed: an identified local caller is served without one.'
    }
    catch [System.UnauthorizedAccessException] {
        Write-Host 'RESULT: ACCESS DENIED to the pipe.' -ForegroundColor Red
        Write-Host 'The DACL shuts this user out: the GUI would not be able to connect.'
    }
    catch [System.TimeoutException] {
        Write-Host 'RESULT: the pipe does not exist (timeout).' -ForegroundColor Yellow
        Write-Host 'The service is not running, or the local channel is disabled.'
    }
    finally {
        $pipe.Dispose()
    }

    return
}

if (-not (Test-Elevation)) {
    Write-Error @'
This action requires a PowerShell running as administrator.
Open an elevated one and run the same command again.
'@
    return
}

if ($Action -eq 'Uninstall') {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue

    if ($service) {
        if ($service.Status -ne 'Stopped') {
            Stop-Service -Name $serviceName -Force
            $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        }

        Remove-Service -Name $serviceName
        Write-Host "Service '$serviceName' removed."
    }
    else {
        Write-Host "The service '$serviceName' was not registered."
    }

    # The files ARE deleted, which was not the case before. The comment that stood here said
    # they held the history database: that was FALSE. The history lives in the service
    # account's profile and the credentials under ProgramData, so only binaries were left here.
    #
    # Leaving them there was costly. This is the SAME folder the MSI installs into, and the
    # binaries copied by hand come from "dotnet publish" without a runtime identifier: they
    # carry a System.ServiceProcess.ServiceController.dll that throws
    # PlatformNotSupportedException on Windows. When the versions match, Windows Installer does
    # NOT replace a file that is already there, so the MSI inherited that broken copy, the
    # service did not start, and the installation stopped with error 1920 - which talks about
    # insufficient privileges and does not name the real cause.
    if (Test-Path $Destination) {
        Remove-Item -Path (Join-Path $Destination '*') -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "Removed the binaries from '$Destination'."
    }

    Write-Host 'History and credentials are NOT here and stay where they are: the history in the'
    Write-Host "service account's profile, the credentials under C:\ProgramData\Observer."
    return
}

# --- Install ---

$Source = (Resolve-Path -Path $Source).Path
$sourceExecutable = Join-Path $Source 'Observer.Service.exe'

if (-not (Test-Path $sourceExecutable)) {
    Write-Error "Cannot find '$sourceExecutable'. Run this first: dotnet publish src/Observer.Service -c Release -o artifacts/service"
    return
}

# appsettings.Local.json is NO longer required. Since plan 3 the service generates and keeps
# its own machine token on first start, under C:\ProgramData\Observer: that is exactly why
# this script does not need to know any secret.
# If the file is there anyway, it is copied and its permissions are restricted, because a token
# set by hand wins over the store and must be protected like the generated one.
$localSettings = Join-Path $Source 'appsettings.Local.json'
# An EMPTY file counts as absent, exactly as the service treats it: emptying it is the
# natural way to remove the token, and announcing 'found a token' on zero bytes would
# mislead the reader.
$carriesToken = (Test-Path $localSettings) -and
    -not [string]::IsNullOrWhiteSpace((Get-Content $localSettings -Raw -ErrorAction SilentlyContinue))

if ($carriesToken) {
    Write-Host 'Found appsettings.Local.json: the token it contains will win over the generated one.'
}
else {
    Write-Host 'No token set by hand: the service will generate its own, under ProgramData.'
}

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue

if ($existing) {
    Write-Host "The service already exists: stopping and removing it before reinstalling."

    if ($existing.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }

    Remove-Service -Name $serviceName
    Start-Sleep -Seconds 2
}

if (-not (Test-Path $Destination)) {
    New-Item -ItemType Directory -Path $Destination | Out-Null
}

Copy-Item -Path (Join-Path $Source '*') -Destination $Destination -Recurse -Force
Write-Host "Files copied to '$Destination'."

# If a token set by hand is there, it must be protected: the installation folder is readable
# by anyone with an account on the machine. Inheritance is removed and access is granted only
# to SYSTEM, which runs the service, and to administrators, who must be able to change it.
# The token the service GENERATES is not handled here: it is already stored, protected,
# under ProgramData.
if ($carriesToken) {
    $installedSettings = Join-Path $Destination 'appsettings.Local.json'
    & icacls.exe $installedSettings /inheritance:r /grant 'NT AUTHORITY\SYSTEM:(R)' /grant 'BUILTIN\Administrators:(F)' | Out-Null
    Write-Host "Permissions restricted on '$installedSettings' (SYSTEM and administrators only)."
}

$installedExecutable = Join-Path $Destination 'Observer.Service.exe'

# No -Credential: without it, New-Service registers the service as LocalSystem, which is
# exactly the intended account. The quotes inside the string are needed because the path
# contains spaces.
New-Service -Name $serviceName `
            -BinaryPathName ('"' + $installedExecutable + '"') `
            -DisplayName 'Observer metrics service' `
            -Description 'Samples CPU and memory and serves them over HTTP and a local named pipe.' `
            -StartupType Automatic | Out-Null

Start-Service -Name $serviceName
Start-Sleep -Seconds 3

Show-Status