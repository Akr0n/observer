<#
.SYNOPSIS
    Runs the real Observer.Service as LocalSystem against hostile credential folders and reports
    what it did.

.DESCRIPTION
    WHY THIS EXISTS. The credential-directory guard (WindowsDirectoryTrust) was written and tested
    from sessions with no elevation, where a folder owned by a foreign account cannot even be built
    and where SYSTEM's rights over C:\ProgramData cannot be exercised. Every claim that depends on
    those rights was reasoned, not measured. This script measures them. It registers throwaway
    services that run the REAL Observer.Service.exe as LocalSystem - the account and the code path
    of production - each one pointed at a folder built to look like one of the cases the release
    notes describe, and records what happened.

    WHAT IT MEASURES. Every item is one or more lines in the report.
      - LocalSystem can remove an EMPTY folder that a standard user owns, also when it is read-only,
        and the folder that comes back is a NEW one (a hidden stream on the old one is gone).
      - An empty folder whose owner took SYSTEM out of its permissions: what the service does.
      - An empty folder that something holds open (a shell sitting in it): the refusal, and the
        start that works once it is released.
      - A non-empty folder nobody vouches for is refused, and left EXACTLY as it was, the planted
        file included; the message carries the recovery, and it is in the Application log.
      - A folder left by the service run by hand (an extra account in its permissions) is refused.
      - The three icacls lines the refusal prints bring a refused folder back to a service that
        starts - /setowner included, which cannot be tried without elevation - and they also reset
        the FILE inside, whose own owner and permissions were hostile.
      - Deleting the folder, the other way out, gives a service that makes its own token.
      - A file copied into a folder the service made itself is adopted as before (the control).
      - A token that was adopted is accepted from the network, and one that was not, is not.
      - Whether Windows restarts a service that dies this way, and what it writes when it does.
    WHAT IT DOES NOT MEASURE. The MSI: an upgrade that stops with error 1920 and rolls back. That
    needs an installed previous version and belongs in a disposable virtual machine.

    WHAT IT TOUCHES, AND WHAT IT NEVER DOES. Everything it creates lives in ONE folder,
    C:\ProgramData\ObserverProbe-<six hex digits>, made protected (SYSTEM and Administrators only)
    in the very call that creates it and checked before anything is put inside. The service
    binaries, the hostile folders and the credential files all sit under it, so no OTHER account
    can plant a link or swap a file in any of the paths an elevated step is about to use. (Your own
    account can: the "hostile" folders are owned by it, or name it, because that is the only
    untrusted account an administrator's session can hand a folder to. A program running as you
    could therefore touch them for the minutes the run lasts.) Services are named
    ObserverProbe-<same digits>-<scenario>, and so are their pipes. It never touches the
    service named Observer, the folder C:\ProgramData\Observer or the installed program files, it
    gives each probe its own port and pipe, and it does not write the history database. At the end
    it compares a snapshot of the installed service, its data folder and its program folder with
    one taken before, and says so if it could not read either. It removes what it created - the
    services, the folders and the files - also when a step fails, and anything it could not remove
    is listed by name, with the reason; -Cleanup removes what an interrupted run left behind. What
    Windows itself keeps is not its to remove: crash reports of the services that died on purpose,
    event log entries, and the key files a service process may leave in the machine's key store.

    THE TOKENS ARE RANDOM PER RUN and never printed. While a probe runs it listens on 0.0.0.0 with
    a token that is valid from the network, exactly as the product does; a fresh random one makes
    that a few seconds of a value nobody could have known in advance. Each probe is started with an
    EMPTY Observer:ApiToken on its command line, so a token set in the machine's environment or in an
    appsettings.Local.json cannot make the service skip the credential folder being measured.

    A BUILD FROM BEFORE THE FIX. To see that this harness CAN fail, run it once against the code
    before 0.24.1 (a "dotnet publish" of the v0.24.0 tag, passed with -ServiceDirectory): planted,
    opendacl, byhand, lockedempty and recipe are expected to FAIL there, because that service
    repaired the folder and read whatever was inside.

    Requires an ELEVATED PowerShell (Windows PowerShell 5.1 or PowerShell 7). Takes about ten
    minutes. RECOMMENDED: publish first from a normal, non-elevated shell, so nothing is built as
    Administrator:
        dotnet publish src\Observer.Service -c Release -r win-x64 --self-contained true -o $env:TEMP\observer-probe-publish
    then run this script with -ServiceDirectory pointing at that folder. Without it the script
    publishes by itself, elevated. The folder given is COPIED into the protected one: it is refused
    if it holds a link, hashed before and after the copy so that a file swapped while it is copied
    is noticed, and the copy is checked for links and for accounts other than SYSTEM and
    Administrators in its permissions. What happened to the folder between your publish and this run
    cannot be seen from here: it is trusted as your own build, and a program running as you could
    change it in that gap. Publishing inside the elevated run closes the gap and builds as
    Administrator instead.

    If the script is refused by the execution policy ("running scripts is disabled"), start it as
        powershell -ExecutionPolicy Bypass -File .\scripts\probe-credential-directory.ps1 -ServiceDirectory ...
    which changes nothing on the machine.

.PARAMETER ReportPath
    Where to write the Markdown report. Default: observer-localsystem-probe.md in the user's TEMP.
    A relative path is taken from the PowerShell location, and the path is tried before anything is
    changed. The report holds no token and no hash of one.

.PARAMETER ServiceDirectory
    An existing "dotnet publish" output of src/Observer.Service (with Observer.Service.exe in it).

.PARAMETER Only
    Run only these scenarios, by name. See -Plan for the names.

.PARAMETER Plan
    Prints what the script would do and stops. Changes nothing and does not need elevation.

.PARAMETER Cleanup
    Removes the services and folders that an interrupted run left behind, and stops.

.PARAMETER Yes
    Do not ask for confirmation.

.EXAMPLE
    # From an ELEVATED PowerShell, in the repository:
    .\scripts\probe-credential-directory.ps1 -ServiceDirectory $env:TEMP\observer-probe-publish

.EXAMPLE
    .\scripts\probe-credential-directory.ps1 -Plan

.EXAMPLE
    .\scripts\probe-credential-directory.ps1 -Cleanup
#>
[CmdletBinding()]
param(
    [string] $ReportPath = (Join-Path $env:TEMP 'observer-localsystem-probe.md'),
    [string] $ServiceDirectory,
    [string[]] $Only,
    [switch] $Plan,
    [switch] $Cleanup,
    [switch] $Yes
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

# powershell -File hands "-Only a,b" over as the single string "a,b": split it, whichever way it came.
$Only = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

$script:Prefix = 'ObserverProbe-'
$script:ProgramData = [Environment]::GetFolderPath('CommonApplicationData')
$script:SystemSid = 'S-1-5-18'
$script:AdminsSid = 'S-1-5-32-544'
$script:Trusted = @('S-1-5-18', 'S-1-5-32-544')
$script:Me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$script:Run = $null
$script:Root = $null
$script:ServiceExe = $null
$script:PlantedToken = $null
$script:CopiedToken = $null
$script:Checks = $null
$script:Leftovers = New-Object Collections.Generic.List[string]
$script:HeldProcesses = New-Object Collections.Generic.List[object]

# What the service says, matched as whole phrases. "is not empty" alone also occurs in the message
# for a failed replacement on an English machine, so the verdict in brackets is part of the needle.
$script:RefusalNeedle = 'is not empty, and as it stands nothing vouches for what is in it'
$script:ReplaceFailureNeedle = 'cannot hold a secret as it stands'
$script:CreateFailureNeedle = 'could not create its credential directory'

# ---------------------------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------------------------

function Test-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Native tools write to stderr and return non-zero exit codes that are DATA here, not failures.
# Under $ErrorActionPreference = 'Stop', Windows PowerShell 5.1 turns any stderr line of a native
# command into a terminating error when it is merged with 2>&1, hence the local override.
function Invoke-Native {
    param([string] $File, [string[]] $Arguments)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        # Windows PowerShell 5.1 wraps a native tool's stderr lines in error records, and Out-String
        # would print them with "At line:... CategoryInfo..." decoration; only the text is wanted.
        $output = & $File @Arguments 2>&1 | ForEach-Object {
            if ($_ -is [Management.Automation.ErrorRecord]) { $_.Exception.Message } else { [string] $_ }
        } | Out-String
        return [pscustomobject]@{ Code = $LASTEXITCODE; Output = $output.Trim() }
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

function Get-FreePort {
    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try {
        return ([Net.IPEndPoint] $listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

# The attributes of a path WITHOUT following a link, or $null if there is nothing there. It does
# not use Test-Path: that follows links, and says "no" for a dangling one that is very much there.
function Get-PathAttributes {
    param([string] $Path)
    try {
        return [IO.File]::GetAttributes($Path)
    }
    catch [IO.FileNotFoundException] {
        return $null
    }
    catch [IO.DirectoryNotFoundException] {
        return $null
    }
}

function Test-IsLink {
    param([string] $Path)
    $attributes = Get-PathAttributes $Path
    if ($null -eq $attributes) { return $false }
    return [bool] ($attributes -band [IO.FileAttributes]::ReparsePoint)
}

# What the service itself looks at - the owner, whether the permissions inherit, who is named - for a
# folder or a file. A link is reported as a link and never read through.
function Get-PathFacts {
    param([string] $Path)
    $attributes = Get-PathAttributes $Path
    if ($null -eq $attributes) {
        return [pscustomobject]@{
            Exists = $false; IsDirectory = $false; IsLink = $false; ReadOnly = $false; Readable = $true
            OwnerSid = ''; Protected = $false; Sids = @(); Entries = @(); Listable = $true; Sddl = ''
        }
    }
    $isDirectory = [bool] ($attributes -band [IO.FileAttributes]::Directory)
    $isLink = [bool] ($attributes -band [IO.FileAttributes]::ReparsePoint)
    $owner = ''
    $protected = $false
    $sids = @()
    $sddl = ''
    $readable = $true
    $entries = @()
    $listable = $true
    if (-not $isLink) {
        try {
            $acl = Get-Acl -LiteralPath $Path
            $owner = [string] $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
            $protected = [bool] $acl.AreAccessRulesProtected
            $sddl = [string] $acl.Sddl
            $sids = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) |
                ForEach-Object { $_.IdentityReference.Value } | Sort-Object -Unique)
        }
        catch {
            $readable = $false
        }
        if ($isDirectory) {
            try {
                $entries = @([IO.Directory]::GetFileSystemEntries($Path) |
                    ForEach-Object { [IO.Path]::GetFileName($_) } | Sort-Object)
            }
            catch {
                $listable = $false
            }
        }
    }
    return [pscustomobject]@{
        Exists      = $true
        IsDirectory = $isDirectory
        IsLink      = $isLink
        ReadOnly    = [bool] ($attributes -band [IO.FileAttributes]::ReadOnly)
        Readable    = $readable
        OwnerSid    = $owner
        Protected   = $protected
        Sids        = $sids
        Entries     = $entries
        Listable    = $listable
        Sddl        = $sddl
    }
}

# The same rule DirectoryTrust.Evaluate applies for LocalSystem: trusted owner, protected DACL,
# nobody but the trusted principals named in it.
function Test-Safe {
    param($Facts)
    if (-not $Facts.Exists -or -not $Facts.IsDirectory -or $Facts.IsLink -or -not $Facts.Readable) { return $false }
    if ($script:Trusted -notcontains $Facts.OwnerSid) { return $false }
    if (-not $Facts.Protected) { return $false }
    foreach ($sid in $Facts.Sids) {
        if ($script:Trusted -notcontains $sid) { return $false }
    }
    return $true
}

# A FILE is judged on its owner and on who its permissions name; whether it inherits does not matter.
function Test-SafeFile {
    param($Facts)
    if (-not $Facts.Exists -or $Facts.IsDirectory -or $Facts.IsLink -or -not $Facts.Readable) { return $false }
    if ($script:Trusted -notcontains $Facts.OwnerSid) { return $false }
    foreach ($sid in $Facts.Sids) {
        if ($script:Trusted -notcontains $sid) { return $false }
    }
    return $true
}

function Format-Facts {
    param($Facts)
    if (-not $Facts.Exists) { return 'absent' }
    if ($Facts.IsLink) { return 'a link (reparse point)' }
    $line = 'owner={0} protected={1} readOnly={2} sids=[{3}]' -f $Facts.OwnerSid, $Facts.Protected, $Facts.ReadOnly, ($Facts.Sids -join ',')
    if ($Facts.IsDirectory) { $line += ' entries=[' + ($Facts.Entries -join ',') + ']' }
    if (-not $Facts.Readable) { $line += ' (permissions NOT readable)' }
    if (-not $Facts.Listable) { $line += ' (listing NOT possible)' }
    return $line
}

# The part of the facts that a service that changed nothing must leave alone.
function Get-Fingerprint {
    param($Facts)
    return ('{0}|{1}|{2}|{3}|{4}|{5}' -f $Facts.Exists, $Facts.OwnerSid, $Facts.Protected, $Facts.ReadOnly, ($Facts.Sids -join ','), ($Facts.Entries -join ','))
}

function Get-Excerpt {
    param([string] $Text, [string] $Needle, [int] $Before = 60, [int] $After = 260)
    if ([string]::IsNullOrEmpty($Text)) { return '' }
    $flat = ($Text -replace '\s+', ' ')
    $at = $flat.IndexOf($Needle, [StringComparison]::Ordinal)
    if ($at -lt 0) { $at = 0; $Before = 0 }
    $start = [Math]::Max(0, $at - $Before)
    $length = [Math]::Min($flat.Length - $start, $Before + $After)
    return $flat.Substring($start, $length)
}

function Add-Check {
    param([string] $Name, $Pass, [string] $Evidence)
    $script:Checks.Add([pscustomobject]@{ Name = $Name; Pass = $Pass; Evidence = $Evidence })
}

# A precondition is a GATE, not a row: a scenario that did not manage to build the case it is about
# stops here, instead of measuring something else and reporting it under the wrong name.
function Assert-Precondition {
    param([bool] $Condition, [string] $What, [string] $Evidence)
    if (-not $Condition) { throw ('PRECONDITION NOT MET - {0}. {1}' -f $What, $Evidence) }
}

function New-Token {
    return 'probe-' + [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
}

# ---------------------------------------------------------------------------------------------
# Folders and files
# ---------------------------------------------------------------------------------------------

# A path this script may delete or take ownership of: the run folder, or something inside it, and
# nothing else. Every destructive step starts here.
function Test-InProbeArea {
    param([string] $Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    if ($Path.Contains('..')) { return $false }
    # -cmatch: the names it makes are lower case, and anything else is not its own. The ProgramData
    # part is the one thing that may differ in case, as Windows paths do.
    $head = '^(?i:' + [regex]::Escape($script:ProgramData + '\') + ')' + [regex]::Escape($script:Prefix)
    return [bool] ($Path -cmatch ($head + '[0-9a-f]{6}(\\[^\\]+)*$'))
}

function Test-ProbeRootName {
    param([string] $Name)
    return [bool] ($Name -cmatch ('^' + [regex]::Escape($script:Prefix) + '[0-9a-f]{6}$'))
}

# Creates the folder ALREADY protected: one call, with the descriptor in it, exactly as the service
# creates its own. CreateDirectory on a path that exists is a silent no-op, so the caller checks
# what came out; a folder that was there before is refused here.
function New-ProtectedDirectory {
    param([string] $Path, [string[]] $Sids)
    if ($null -ne (Get-PathAttributes $Path)) { throw "Already exists, not creating it: $Path" }
    $security = New-Object Security.AccessControl.DirectorySecurity
    $security.SetAccessRuleProtection($true, $false)
    $inherit = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit
    foreach ($sid in $Sids) {
        $identity = New-Object Security.Principal.SecurityIdentifier($sid)
        $rule = New-Object Security.AccessControl.FileSystemAccessRule(
            $identity, [Security.AccessControl.FileSystemRights]::FullControl, $inherit,
            [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow)
        $security.AddAccessRule($rule)
    }
    $info = New-Object IO.DirectoryInfo($Path)
    if ($PSVersionTable.PSEdition -eq 'Core') {
        [IO.FileSystemAclExtensions]::Create($info, $security)
    }
    else {
        $info.Create($security)
    }
}

# Every entry under the path that is a link, without following one, or that cannot be listed.
function Get-UnsafeEntries {
    param([string] $Path)
    $found = New-Object Collections.Generic.List[string]
    $pending = New-Object Collections.Generic.Stack[string]
    $pending.Push($Path)
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        try {
            $names = [IO.Directory]::GetFileSystemEntries($current)
        }
        catch {
            $found.Add($current + ' (cannot be listed: ' + $_.Exception.GetType().Name + ')')
            continue
        }
        foreach ($name in $names) {
            $attributes = [IO.File]::GetAttributes($name)
            if ($attributes -band [IO.FileAttributes]::ReparsePoint) {
                $found.Add($name)
            }
            elseif ($attributes -band [IO.FileAttributes]::Directory) {
                $pending.Push($name)
            }
        }
    }
    return , $found.ToArray()
}

# Everything under the path, a file or a folder, owned by a trusted account and naming nobody else,
# with no link anywhere. The gate in front of running anything from it as LocalSystem.
function Get-UntrustedInTree {
    param([string] $Path)
    $problems = New-Object Collections.Generic.List[string]
    foreach ($entry in (Get-UnsafeEntries $Path)) { $problems.Add('link or unlistable: ' + $entry) }
    $targets = New-Object Collections.Generic.List[string]
    $targets.Add($Path)
    if ($problems.Count -eq 0) {
        foreach ($item in (Get-ChildItem -LiteralPath $Path -Recurse -Force)) { $targets.Add($item.FullName) }
    }
    foreach ($target in $targets) {
        $facts = Get-PathFacts $target
        if (-not $facts.Readable) { $problems.Add('permissions unreadable: ' + $target); continue }
        if ($script:Trusted -notcontains $facts.OwnerSid) { $problems.Add(('owner {0}: {1}' -f $facts.OwnerSid, $target)) }
        foreach ($sid in $facts.Sids) {
            if ($script:Trusted -notcontains $sid) { $problems.Add(('names {0}: {1}' -f $sid, $target)) }
        }
    }
    return , $problems.ToArray()
}

function Set-OwnerTo {
    param([string] $Path, [string] $Sid)
    $result = Invoke-Native 'icacls.exe' @($Path, '/setowner', "*$Sid")
    $owner = (Get-PathFacts $Path).OwnerSid
    if ($result.Code -ne 0 -or $owner -ne $Sid) {
        throw ('PRECONDITION NOT MET - could not make {0} owned by {1}: the owner is {2} (icacls exit {3}: {4})' -f $Path, $Sid, $owner, $result.Code, $result.Output)
    }
}

function New-ScenarioDirectory {
    param([string] $Name)
    $path = Join-Path $script:Root $Name
    if ($null -ne (Get-PathAttributes $path)) { throw "PRECONDITION NOT MET - $path is already there." }
    New-Item -ItemType Directory -Path $path | Out-Null
    return $path
}

# A hidden stream on a folder: invisible to the listing (the folder still counts as EMPTY) and gone
# with the folder. If it survives, the folder was repaired in place; if it is gone, it was replaced.
function Add-Witness {
    param([string] $Directory)
    Set-Content -LiteralPath $Directory -Stream 'witness' -Value 'x'
}

function Test-Witness {
    param([string] $Directory)
    try {
        return ((Get-Content -LiteralPath $Directory -Stream 'witness' -ErrorAction Stop) -eq 'x')
    }
    catch {
        return $false
    }
}

function Add-PlantedStore {
    param([string] $Directory, [string] $Token)
    $json = '{"current":"' + $Token + '","previous":null,"previousExpiresAt":null}'
    [IO.File]::WriteAllText((Join-Path $Directory 'credentials.json'), $json)
}

function Read-Store {
    param([string] $Directory)
    $path = Join-Path $Directory 'credentials.json'
    if ($null -eq (Get-PathAttributes $path)) { return '' }
    return [IO.File]::ReadAllText($path)
}

function Get-StoreToken {
    param([string] $Directory)
    $text = Read-Store $Directory
    if ([string]::IsNullOrWhiteSpace($text)) { return '' }
    try { return [string] (ConvertFrom-Json $text).current } catch { return '' }
}

function Set-ReadOnlyFlag {
    param([string] $Path)
    $item = Get-Item -LiteralPath $Path -Force
    $item.Attributes = $item.Attributes -bor [IO.FileAttributes]::ReadOnly
}

# Returns $true when the path is gone. Anything else is recorded as a leftover, by name and reason:
# this script does not get to say "everything was removed" without having looked.
function Remove-ProbePath {
    param([string] $Path)
    if (-not (Test-InProbeArea $Path)) { throw "Refusing to remove something outside the probe area: $Path" }
    if ($null -eq (Get-PathAttributes $Path)) { return $true }
    if (Test-IsLink $Path) {
        # Removes the link, never what it points to: a non-recursive delete of a reparse point.
        try { [IO.Directory]::Delete($Path, $false) } catch { }
        if ($null -eq (Get-PathAttributes $Path)) { return $true }
        $script:Leftovers.Add("$Path is a link and could not be removed")
        return $false
    }
    # Only a folder has a tree to look through: on a plain file the listing throws, and that used to be
    # taken for "something unlistable in it" - a false leftover on every run.
    $isDirectory = [bool] ((Get-PathAttributes $Path) -band [IO.FileAttributes]::Directory)
    if ($isDirectory) {
        $unsafe = Get-UnsafeEntries $Path
        if ($unsafe.Count -gt 0) {
            $script:Leftovers.Add("$Path was left in place: it contains " + ($unsafe -join '; ') + '. Remove those by hand.')
            return $false
        }
    }
    Invoke-Native 'icacls.exe' @($Path, '/setowner', ('*' + $script:AdminsSid), '/T', '/C') | Out-Null
    Invoke-Native 'icacls.exe' @($Path, '/grant', ('*' + $script:AdminsSid + ':(OI)(CI)F'), '/T', '/C') | Out-Null
    # The read-only flag stops a delete even with full control; clear it on the folder and inside.
    $everything = @($Path)
    if ($isDirectory) {
        $everything += @(Get-ChildItem -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
    }
    foreach ($one in $everything) {
        try {
            $item = Get-Item -LiteralPath $one -Force -ErrorAction Stop
            $item.Attributes = $item.Attributes -band (-bnot [IO.FileAttributes]::ReadOnly)
        }
        catch { }
    }
    # A virus scanner or an indexer can hold a freshly written file for a moment: a few tries, and the
    # failure is recorded only after the last one.
    $failure = ''
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            $failure = ''
            break
        }
        catch {
            $failure = $_.Exception.Message
            Start-Sleep -Milliseconds 500
        }
    }
    if ($failure.Length -gt 0 -or $null -ne (Get-PathAttributes $Path)) {
        if ($failure.Length -eq 0) { $failure = 'it is still there after the removal' }
        $script:Leftovers.Add("$Path could not be removed: " + $failure)
        return $false
    }
    # Withdraw an earlier entry about this path (or something under it): it is gone now.
    $null = $script:Leftovers.RemoveAll([Predicate[string]] { param($entry) $entry.StartsWith($Path) })
    return $true
}

# ---------------------------------------------------------------------------------------------
# Services, events, the network
# ---------------------------------------------------------------------------------------------

function New-Probe {
    param([string] $Name, [string] $Directory, [switch] $WithRecovery)
    $service = $script:Run + '-' + $Name
    $store = Join-Path $Directory 'credentials.json'
    $port = Get-FreePort
    # Everything the real service would share with the installed one is redirected: the store, the
    # HTTPS port, the pipe name, and no history database. The executable is quoted; the paths in
    # the arguments hold no spaces, which the preflight checks.
    #
    # --Observer:ApiToken= (EMPTY) is not decoration. A token in configuration - Observer__ApiToken in
    # the machine's environment, an appsettings.Local.json next to the exe - makes the service skip the
    # credential folder altogether, and then no scenario measures anything and the real token would be
    # served on 0.0.0.0. The command line is read last, so an empty value beats both, and a blank token
    # counts as "not configured".
    $command = '"{0}" --Observer:CredentialStorePath={1} --Observer:Network:HttpsPort={2} --Observer:LocalChannel:PipeName={3} --Observer:Storage:Enabled=false --Observer:ApiToken=' -f
        $script:ServiceExe, $store, $port, $service
    New-Service -Name $service -BinaryPathName $command -DisplayName $service -StartupType Manual | Out-Null
    $recovery = 'not configured'
    if ($WithRecovery) {
        # The recovery actions the MSI configures: restart after five seconds, on the first, the
        # second and every later failure. The failure-flag is left at its default, as the MSI does.
        $set = Invoke-Native 'sc.exe' @('failure', $service, 'reset=', '86400', 'actions=', 'restart/5000/restart/5000/restart/5000')
        $read = Invoke-Native 'sc.exe' @('qfailure', $service)
        $recovery = ('sc failure exit {0}; read back: {1}' -f $set.Code, ($read.Output -replace '\s+', ' '))
    }
    return [pscustomobject]@{ Service = $service; Directory = $Directory; Store = $store; Port = $port; Recovery = $recovery }
}

# 'Gone' is said ONLY when the Service Control Manager itself says there is no such service: sc query
# answers 1060 (ERROR_SERVICE_DOES_NOT_EXIST) for that, in any language. Get-Service cannot be used to
# decide it - it returns nothing both for a service that is not there and for an SCM that did not
# answer - and neither can a failed WMI query (the service is down, a timeout). Any other answer is
# 'Unknown', which is never "gone": taking it for gone would skip the delete and leave a LocalSystem
# service registered on a folder that is about to be removed, where a standard user could put whatever
# they like. (1072, marked for deletion, is 'Unknown' too: it is still registered.)
function Get-ServiceSample {
    param([string] $Name)
    $query = Invoke-Native 'sc.exe' @('query', $Name)
    if ($query.Code -eq 1060) { return [pscustomobject]@{ Status = 'Gone'; ProcessId = 0; ExitCode = -1 } }
    if ($query.Code -ne 0) { return [pscustomobject]@{ Status = 'Unknown'; ProcessId = 0; ExitCode = -1 } }
    try {
        $cim = Get-CimInstance -ClassName Win32_Service -Filter ("Name = '{0}'" -f $Name) -ErrorAction Stop
    }
    catch {
        $cim = $null
    }
    if ($null -ne $cim) {
        return [pscustomobject]@{ Status = [string] $cim.State; ProcessId = [int] $cim.ProcessId; ExitCode = [int] $cim.ExitCode }
    }
    # The SCM says it exists but WMI would not say more: its state, without a process id.
    $scm = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $scm) { return [pscustomobject]@{ Status = 'Unknown'; ProcessId = 0; ExitCode = -1 } }
    return [pscustomobject]@{ Status = [string] $scm.Status; ProcessId = 0; ExitCode = -1 }
}

function Get-EventText {
    param($Event)
    if (-not [string]::IsNullOrEmpty($Event.Message)) { return [string] $Event.Message }
    return (@($Event.Properties | ForEach-Object { [string] $_.Value }) -join ' ')
}

# Everything the two logs recorded since a moment that mentions the probe's service or its folder.
function Get-ProbeEvents {
    param([datetime] $Since, [string] $Service, [string] $Folder)
    $found = New-Object Collections.Generic.List[object]
    foreach ($log in @('Application', 'System')) {
        $events = @(Get-WinEvent -FilterHashtable @{ LogName = $log; StartTime = $Since } -ErrorAction SilentlyContinue)
        foreach ($event in $events) {
            $text = Get-EventText $event
            if ($text.Contains($Service) -or $text.Contains("'" + $Folder + "'")) {
                $found.Add([pscustomobject]@{ Log = $log; Id = [int] $event.Id; Provider = [string] $event.ProviderName; Text = $text })
            }
        }
    }
    return , $found.ToArray()
}

# Starts the service and watches it: whether it ever reached Running, the process ids it went through
# (one per start, so a restart by Windows shows as a second id), and what the logs recorded.
function Start-Probe {
    param($Probe, [int] $WaitSeconds, [int] $ObserveSeconds = 0)
    $since = (Get-Date).AddSeconds(-2)
    $start = Invoke-Native 'sc.exe' @('start', $Probe.Service)
    $pids = New-Object Collections.Generic.List[int]
    $sawRunning = $false
    $exitCode = -1
    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    do {
        $sample = Get-ServiceSample $Probe.Service
        if ($sample.ProcessId -ne 0 -and -not $pids.Contains($sample.ProcessId)) { $pids.Add($sample.ProcessId) }
        if ($sample.Status -eq 'Running') { $sawRunning = $true }
        $exitCode = $sample.ExitCode
        Start-Sleep -Milliseconds 400
    } while ((Get-Date) -lt $deadline -and -not $sawRunning)
    if ($ObserveSeconds -gt 0) {
        $until = (Get-Date).AddSeconds($ObserveSeconds)
        while ((Get-Date) -lt $until) {
            $sample = Get-ServiceSample $Probe.Service
            if ($sample.ProcessId -ne 0 -and -not $pids.Contains($sample.ProcessId)) { $pids.Add($sample.ProcessId) }
            $exitCode = $sample.ExitCode
            Start-Sleep -Milliseconds 400
        }
    }
    # The event log writes a crash a few seconds after it happens.
    if (-not $sawRunning) { Start-Sleep -Seconds 4 }
    $final = Get-ServiceSample $Probe.Service
    return [pscustomobject]@{
        SawRunning  = $sawRunning
        Final       = $final.Status
        ExitCode    = $exitCode
        Pids        = $pids.ToArray()
        StartOutput = ($start.Output -replace '\s+', ' ')
        Events      = (Get-ProbeEvents -Since $since -Service $Probe.Service -Folder $Probe.Directory)
    }
}

function Stop-Probe {
    param($Probe)
    Invoke-Native 'sc.exe' @('stop', $Probe.Service) | Out-Null
    for ($i = 0; $i -lt 30; $i++) {
        $sample = Get-ServiceSample $Probe.Service
        if ($sample.Status -eq 'Gone' -or $sample.Status -eq 'Stopped') { break }
        Start-Sleep -Milliseconds 500
    }
}

function Remove-ProbeService {
    param([string] $Name)
    if ($Name -cnotmatch ('^' + [regex]::Escape($script:Prefix) + '[0-9a-f]{6}-[a-z]+$')) {
        throw "Refusing to remove a service that is not a probe: $Name"
    }
    if ((Get-ServiceSample $Name).Status -eq 'Gone') {
        # Withdraw an earlier entry about this service, if a previous try left one: it is gone now.
        $null = $script:Leftovers.RemoveAll([Predicate[string]] { param($entry) $entry.StartsWith('service ' + $Name + ' ') })
        return $true
    }
    Invoke-Native 'sc.exe' @('stop', $Name) | Out-Null
    for ($i = 0; $i -lt 30; $i++) {
        $status = (Get-ServiceSample $Name).Status
        if ($status -eq 'Stopped' -or $status -eq 'Gone') { break }
        Start-Sleep -Milliseconds 500
    }
    # (An 'Unknown' answer does not stop here: the delete is tried anyway, and the loop below decides.)
    Invoke-Native 'sc.exe' @('delete', $Name) | Out-Null
    # A service that died can leave its process around for a moment. Only ever a probe's, matched by
    # its own pipe name on the command line.
    Get-CimInstance Win32_Process -Filter "Name = 'Observer.Service.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine.Contains('PipeName=' + $Name) } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    for ($i = 0; $i -lt 20; $i++) {
        if ((Get-ServiceSample $Name).Status -eq 'Gone') {
            # Withdraw an earlier entry about this service: it is gone now.
            $null = $script:Leftovers.RemoveAll([Predicate[string]] { param($entry) $entry.StartsWith('service ' + $Name + ' ') })
            return $true
        }
        Start-Sleep -Milliseconds 500
    }
    $entry = "service $Name is still registered (marked for deletion, a process still holds it, or the Service Control Manager did not answer)"
    if (-not $script:Leftovers.Contains($entry)) { $script:Leftovers.Add($entry) }
    return $false
}

# The status code the service answers a request with over HTTPS on loopback: 200 if the token is
# accepted from the network, 401 if not, 0 if nothing answered in time. TCP is the network path,
# whatever the address. curl.exe ships with Windows and behaves the same in both PowerShells, which
# a certificate callback in a script block does not.
function Get-NetworkStatus {
    param([int] $Port, [string] $Token, [int] $WaitSeconds = 20)
    $curl = Join-Path $env:SystemRoot 'System32\curl.exe'
    if (-not (Test-Path -LiteralPath $curl)) { return -1 }
    $code = 0
    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    do {
        $arguments = @('-k', '-s', '-o', 'NUL', '-w', '%{http_code}', '--max-time', '5', '-H', ('Authorization: Bearer ' + $Token), ('https://127.0.0.1:{0}/metrics/catalog' -f $Port))
        $result = Invoke-Native $curl $arguments
        $text = ($result.Output -replace '[^0-9]', '')
        if ($text.Length -gt 0) { $code = [int] $text } else { $code = 0 }
        if ($code -ne 0) { break }
        Start-Sleep -Milliseconds 700
    } while ((Get-Date) -lt $deadline)
    return $code
}

function Start-HeldProcess {
    param([string] $Directory)
    # A process whose current directory is the folder: it holds a handle that allows reading and
    # writing but not deleting, so removing the folder fails with a sharing violation.
    $ping = Join-Path $env:SystemRoot 'System32\PING.EXE'
    $process = Start-Process -FilePath $ping -ArgumentList @('-n', '120', '127.0.0.1') -WorkingDirectory $Directory -WindowStyle Hidden -PassThru
    $script:HeldProcesses.Add([pscustomobject]@{ Id = $process.Id; Started = $process.StartTime })
    Start-Sleep -Milliseconds 800
    return $process
}

function Stop-HeldProcesses {
    # ToArray, not @(...): in PowerShell 7, @() over a List[object] throws "Argument types do not match".
    foreach ($held in $script:HeldProcesses.ToArray()) {
        $process = Get-Process -Id $held.Id -ErrorAction SilentlyContinue
        # Same id AND same start time: an id can be reused by an unrelated program after ours ends.
        if ($process -and $process.StartTime -eq $held.Started) {
            Stop-Process -Id $held.Id -Force -ErrorAction SilentlyContinue
        }
    }
    $script:HeldProcesses.Clear()
    Start-Sleep -Milliseconds 500
}

# ---------------------------------------------------------------------------------------------
# What the installed Observer looked like
# ---------------------------------------------------------------------------------------------

# Opened so that a file another process holds open for writing can still be read.
function Get-FileSha256 {
    param([string] $Path)
    $stream = New-Object IO.FileStream($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try {
        $sha = [Security.Cryptography.SHA256]::Create()
        try { return ([BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '') }
        finally { $sha.Dispose() }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-Sha12 {
    param([string] $Path)
    return (Get-FileSha256 $Path).Substring(0, 12)
}

# One hash for a whole folder: every file's relative path and full SHA-256, in order. Used to see that
# what was copied is what was there.
function Get-TreeHash {
    param([string] $Path)
    $lines = New-Object Collections.Generic.List[string]
    foreach ($file in (Get-ChildItem -LiteralPath $Path -Recurse -Force -File | Sort-Object FullName)) {
        $lines.Add($file.FullName.Substring($Path.Length).TrimStart('\') + '|' + (Get-FileSha256 $file.FullName))
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes($lines -join "`n")
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '').Substring(0, 16) }
    finally { $sha.Dispose() }
}

function Get-TreeSnapshot {
    param([string] $Path)
    $facts = Get-PathFacts $Path
    if (-not $facts.Exists) { return 'absent' }
    if (-not $facts.Readable) { throw "permissions of $Path are not readable" }
    $lines = New-Object Collections.Generic.List[string]
    $lines.Add(('folder sddl={0} written={1}' -f $facts.Sddl, (Get-Item -LiteralPath $Path -Force).LastWriteTimeUtc.Ticks))
    foreach ($item in (Get-ChildItem -LiteralPath $Path -Recurse -Force -File -ErrorAction Stop | Sort-Object FullName)) {
        $one = Get-PathFacts $item.FullName
        $lines.Add(('{0} length={1} written={2} owner={3} sddl={4} sha256={5}' -f $item.FullName.Substring($Path.Length), $item.Length, $item.LastWriteTimeUtc.Ticks, $one.OwnerSid, $one.Sddl, (Get-Sha12 $item.FullName)))
    }
    return ($lines -join "`n")
}

# The 64-bit Program Files, also from a 32-bit PowerShell host, which would otherwise be told the
# (x86) folder and look at a place where Observer is never installed.
function Get-NativeProgramFiles {
    $native = $env:ProgramW6432
    if (-not [string]::IsNullOrEmpty($native)) { return $native }
    return [Environment]::GetFolderPath('ProgramFiles')
}

# A snapshot that cannot be read is NOT verified, and is reported as such: two identical error
# strings must never pass as "unchanged".
function Get-InstalledSnapshot {
    $parts = [ordered]@{}
    $verified = $true
    $nothing = $true
    try {
        $service = Get-CimInstance Win32_Service -Filter "Name = 'Observer'" -ErrorAction Stop
        if ($null -eq $service) {
            $parts['service'] = 'not installed'
        }
        else {
            $nothing = $false
            $started = ''
            if ($service.ProcessId -ne 0) {
                $started = [string] (Get-Process -Id $service.ProcessId -ErrorAction Stop).StartTime.ToUniversalTime().Ticks
            }
            $failure = (Invoke-Native 'sc.exe' @('qfailure', 'Observer')).Output -replace '\s+', ' '
            $parts['service'] = ('state={0} start={1} account={2} path={3} pid={4} processStarted={5}' -f $service.State, $service.StartMode, $service.StartName, $service.PathName, $service.ProcessId, $started)
            $parts['service recovery'] = $failure
        }
    }
    catch {
        $verified = $false
        $parts['service'] = 'UNREADABLE: ' + $_.Exception.Message
    }
    $places = @(
        @{ Key = 'data folder'; Path = (Join-Path $script:ProgramData 'Observer') },
        @{ Key = 'program folder'; Path = (Join-Path (Get-NativeProgramFiles) 'Observer') }
    )
    foreach ($place in $places) {
        try {
            $parts[$place.Key] = Get-TreeSnapshot $place.Path
            if ($parts[$place.Key] -ne 'absent') { $nothing = $false }
        }
        catch {
            $verified = $false
            $parts[$place.Key] = 'UNREADABLE: ' + $_.Exception.Message
        }
    }
    return [pscustomobject]@{ Parts = $parts; Verified = $verified; Nothing = $nothing }
}

# The names of the parts that differ. Empty means unchanged AS FAR AS THE SNAPSHOT SEES, which the
# caller only says when both snapshots were readable.
function Compare-Snapshots {
    param($Before, $After)
    $differences = New-Object Collections.Generic.List[string]
    foreach ($key in $Before.Parts.Keys) {
        if ($Before.Parts[$key] -ne $After.Parts[$key]) { $differences.Add($key) }
    }
    return , $differences.ToArray()
}

# ---------------------------------------------------------------------------------------------
# Recovery, as the refusal prints it
# ---------------------------------------------------------------------------------------------

function Get-RecoveryCommands {
    param([string] $Directory)
    return @(
        @('icacls.exe', @($Directory, '/setowner', '*S-1-5-32-544', '/T')),
        @('icacls.exe', @($Directory, '/reset', '/T')),
        @('icacls.exe', @($Directory, '/inheritance:r', '/grant:r', '*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F'))
    )
}

# The message the operator sees is the only text they have in front of them, so it has to carry the
# recovery lines with the real path. Checked against what the service actually wrote to the log.
function Get-MissingRecoveryText {
    param([string] $Message, [string] $Directory)
    $needles = @(
        ('icacls "{0}" /setowner "*S-1-5-32-544" /T' -f $Directory),
        ('icacls "{0}" /reset /T' -f $Directory),
        ('icacls "{0}" /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F"' -f $Directory),
        ('takeown /F "{0}" /A /R' -f $Directory)
    )
    # Contains, not -like: the commands hold a literal asterisk (*S-1-5-32-544), which -like would
    # read as a wildcard and quietly accept a message that lacks it.
    #
    # The leading comma is not decoration. A function's output is unrolled, so an EMPTY array comes
    # out as $null, and under Set-StrictMode -Version 2 the caller's $x.Count then THROWS - in the one
    # case that means "nothing is missing". The comma hands the array over intact.
    return , @($needles | Where-Object { -not $Message.Contains($_) })
}

function Find-Event {
    param($Result, [string] $Needle)
    foreach ($event in $Result.Events) {
        if ($event.Log -eq 'Application' -and $event.Text.Contains($Needle)) { return $event }
    }
    return $null
}

function Add-EventCheck {
    param([string] $Name, $Event, [string] $Needle)
    if ($null -eq $Event) {
        Add-Check $Name $false 'no such event in the Application log'
        return
    }
    Add-Check $Name $true ('event {0}, source {1}: ...{2}...' -f $Event.Id, $Event.Provider, (Get-Excerpt $Event.Text $Needle))
}

function Add-NoStartCheck {
    param($Result)
    Add-Check 'the service did not start' (-not $Result.SawRunning) ('final state: {0}; process exit code: {1}; sc start said: {2}' -f $Result.Final, $Result.ExitCode, $Result.StartOutput)
}

function Add-ScmInfo {
    param($Result)
    $scm = @($Result.Events | Where-Object { $_.Log -eq 'System' })
    $line = ($scm | ForEach-Object { 'id ' + $_.Id + ' (' + (Get-Excerpt $_.Text '' 0 110) + ')' }) -join ' | '
    Add-Check 'INFO: what the Service Control Manager recorded for it' $null ('{0} event(s): {1}' -f $scm.Count, $line)
}

# ---------------------------------------------------------------------------------------------
# Scenarios. Each builds a case, starts a probe, and records checks with Add-Check.
# ---------------------------------------------------------------------------------------------

$script:Scenarios = @(
    @{
        Name = 'empty'
        Description = 'An EMPTY folder owned by a standard user (it inherits its permissions). Expect: replaced by a protected one, service starts.'
        Run = {
            $dir = New-ScenarioDirectory 'empty'
            Set-OwnerTo $dir $script:Me
            Add-Witness $dir
            $before = Get-PathFacts $dir
            Assert-Precondition ($before.Entries.Count -eq 0 -and -not $before.Protected -and (Test-Witness $dir)) 'an empty, untrusted-owner, inheriting folder with a witness stream' (Format-Facts $before)
            $probe = New-Probe 'empty' $dir
            $result = Start-Probe $probe -WaitSeconds 30
            $after = Get-PathFacts $dir
            Add-Check 'LocalSystem removed the folder and the service started' $result.SawRunning ('final state: ' + $result.Final)
            Add-Check 'the folder is now a protected one, owned by SYSTEM or Administrators' (Test-Safe $after) (Format-Facts $after)
            Add-Check 'it is a NEW folder, replaced and not repaired: the hidden stream is gone' (-not (Test-Witness $dir)) 'the witness stream was on the old folder'
            Add-Check 'the service created credentials.json and certificate.pfx' (($after.Entries -contains 'credentials.json') -and ($after.Entries -contains 'certificate.pfx')) ('entries: ' + ($after.Entries -join ', '))
            $noise = Find-Event $result $script:ReplaceFailureNeedle
            $refused = Find-Event $result $script:RefusalNeedle
            Add-Check 'nothing was logged as a refusal' (($null -eq $noise) -and ($null -eq $refused)) 'no refusal text in the Application log'
        }
    },
    @{
        Name = 'readonly'
        Description = 'The same, marked read-only. Expect: the flag is cleared, the folder replaced, the service starts.'
        Run = {
            $dir = New-ScenarioDirectory 'readonly'
            Set-OwnerTo $dir $script:Me
            Add-Witness $dir
            Set-ReadOnlyFlag $dir
            $before = Get-PathFacts $dir
            Assert-Precondition ($before.ReadOnly -and $before.Entries.Count -eq 0 -and (Test-Witness $dir)) 'an empty, read-only folder with a witness stream' (Format-Facts $before)
            $probe = New-Probe 'readonly' $dir
            $result = Start-Probe $probe -WaitSeconds 30
            $after = Get-PathFacts $dir
            Add-Check 'LocalSystem cleared the flag, removed the folder and the service started' $result.SawRunning ('final state: ' + $result.Final)
            Add-Check 'the folder is now a protected one, owned by SYSTEM or Administrators, no longer read-only' ((Test-Safe $after) -and -not $after.ReadOnly) (Format-Facts $after)
            Add-Check 'it is a NEW folder: the hidden stream is gone' (-not (Test-Witness $dir)) 'the witness stream was on the old folder'
        }
    },
    @{
        Name = 'lockedempty'
        Description = 'An EMPTY folder whose owner took SYSTEM out of its permissions. The release notes say this one is refused; this measures it.'
        Run = {
            $dir = New-ScenarioDirectory 'lockedempty'
            Set-OwnerTo $dir $script:Me
            Add-Witness $dir
            $lock = Invoke-Native 'icacls.exe' @($dir, '/inheritance:r', '/grant:r', "*$($script:Me):(OI)(CI)F")
            $before = Get-PathFacts $dir
            Assert-Precondition (($lock.Code -eq 0) -and $before.Protected -and ($before.Sids.Count -eq 1) -and ($before.Sids[0] -eq $script:Me) -and ($before.Entries.Count -eq 0)) 'an empty folder that names only its owner' ((Format-Facts $before) + '; icacls said: ' + $lock.Output)
            $probe = New-Probe 'lockedempty' $dir
            $result = Start-Probe $probe -WaitSeconds 12
            $after = Get-PathFacts $dir
            $refusal = Find-Event $result $script:RefusalNeedle
            $replaceFailure = Find-Event $result $script:ReplaceFailureNeedle
            # "Did not start" alone proves nothing here: a blocked or crashing exe would pass it without
            # the guard ever running. The refusal has to be in the log, whichever of the two phrasings
            # the service uses for an empty folder it could not replace (the second is the expected one).
            $named = $refusal
            $needle = $script:RefusalNeedle
            if ($null -eq $named) { $named = $replaceFailure; $needle = $script:ReplaceFailureNeedle }
            Add-Check 'the service did not start, as the release notes say' (-not $result.SawRunning) ('final state: ' + $result.Final + '; exit code ' + $result.ExitCode + '; sc start said: ' + $result.StartOutput)
            Add-EventCheck 'and the reason is the guard: the refusal is in the Application log' $named $needle
            Add-Check 'the folder is left as it was (owner, permissions, entries, hidden stream)' (((Get-Fingerprint $before) -eq (Get-Fingerprint $after)) -and (Test-Witness $dir)) ('before: ' + (Format-Facts $before) + ' | after: ' + (Format-Facts $after))
            if ($null -ne $named) {
                Add-Check 'INFO: what the service said about an EMPTY folder' $null ('event {0}: ...{1}...' -f $named.Id, (Get-Excerpt $named.Text 'The credential directory' 0 330))
            }
            Add-ScmInfo $result
        }
    },
    @{
        Name = 'heldopen'
        Description = 'An EMPTY folder that a process holds open (a shell sitting in it). Expect: refused with the replacement message, nothing deleted; started once released.'
        Run = {
            $dir = New-ScenarioDirectory 'heldopen'
            Set-OwnerTo $dir $script:Me
            Add-Witness $dir
            $holder = Start-HeldProcess $dir
            try {
                Assert-Precondition (-not $holder.HasExited) 'a process is holding the folder' 'it exited at once'
                $before = Get-PathFacts $dir
                Assert-Precondition ($before.Entries.Count -eq 0) 'the folder is empty' (Format-Facts $before)
                $probe = New-Probe 'heldopen' $dir
                $first = Start-Probe $probe -WaitSeconds 12
                $after = Get-PathFacts $dir
                $replaceFailure = Find-Event $first $script:ReplaceFailureNeedle
                Add-Check 'the service did not start' (-not $first.SawRunning) ('final state: ' + $first.Final + '; exit code ' + $first.ExitCode)
                Add-EventCheck 'the reason is in the Application log: "cannot hold a secret as it stands"' $replaceFailure $script:ReplaceFailureNeedle
                Add-Check 'the folder is still there, empty, with the same owner and hidden stream' (((Get-Fingerprint $before) -eq (Get-Fingerprint $after)) -and (Test-Witness $dir)) ('before: ' + (Format-Facts $before) + ' | after: ' + (Format-Facts $after))
            }
            finally {
                Stop-HeldProcesses
            }
            $second = Start-Probe $probe -WaitSeconds 30
            $later = Get-PathFacts $dir
            Add-Check 'once the process has let go, the same service starts and the folder is replaced' ($second.SawRunning -and (Test-Safe $later) -and -not (Test-Witness $dir)) ('final state: ' + $second.Final + '; ' + (Format-Facts $later))
        }
    },
    @{
        Name = 'planted'
        Description = 'A NON-EMPTY folder owned by a standard user, with a planted credentials.json. Expect: refused, left exactly as it was, and what Windows does about it.'
        Run = {
            $dir = New-ScenarioDirectory 'planted'
            Set-OwnerTo $dir $script:Me
            Add-PlantedStore $dir $script:PlantedToken
            Set-OwnerTo (Join-Path $dir 'credentials.json') $script:Me
            $before = Get-PathFacts $dir
            $fileBefore = Get-PathFacts (Join-Path $dir 'credentials.json')
            $storeBefore = Read-Store $dir
            Assert-Precondition (($before.OwnerSid -eq $script:Me) -and ($before.Entries -contains 'credentials.json') -and ($fileBefore.OwnerSid -eq $script:Me)) 'a folder and a file owned by an untrusted account' ((Format-Facts $before) + ' | file: ' + (Format-Facts $fileBefore))
            $probe = New-Probe 'planted' $dir -WithRecovery
            $result = Start-Probe $probe -WaitSeconds 12 -ObserveSeconds 30
            $after = Get-PathFacts $dir
            $fileAfter = Get-PathFacts (Join-Path $dir 'credentials.json')
            $refusal = Find-Event $result ($script:RefusalNeedle + ' (UntrustedOwner)')
            if ($null -eq $refusal) { $refusal = Find-Event $result $script:RefusalNeedle }
            Add-NoStartCheck $result
            Add-Check 'the folder is left EXACTLY as it was (owner, permissions, entries)' ((Get-Fingerprint $before) -eq (Get-Fingerprint $after)) ('before: ' + (Format-Facts $before) + ' | after: ' + (Format-Facts $after))
            Add-Check 'the planted file is untouched: same bytes, same owner, same permissions' (($storeBefore -eq (Read-Store $dir)) -and ($fileBefore.OwnerSid -eq $fileAfter.OwnerSid) -and ($fileBefore.Sddl -eq $fileAfter.Sddl)) ('owner before/after: ' + $fileBefore.OwnerSid + ' / ' + $fileAfter.OwnerSid)
            Add-EventCheck 'the reason is in the Application log: "is not empty ... nothing vouches for what is in it"' $refusal $script:RefusalNeedle
            Add-Check 'the verdict named is UntrustedOwner' (($null -ne $refusal) -and $refusal.Text.Contains('for what is in it (UntrustedOwner)')) 'the verdict in brackets in the message'
            if ($null -ne $refusal) {
                $missing = Get-MissingRecoveryText $refusal.Text $dir
                Add-Check 'the message carries the four recovery commands with the real path' ($missing.Count -eq 0) ('missing: ' + ($missing -join ' || '))
                # Builds up to 0.24.3 print "takeown ... /D Y", which is rejected outside English Windows:
                # this row is what tells a build with that line from one without it.
                Add-Check 'the takeown line does NOT carry /D Y (it is rejected outside English Windows)' (-not $refusal.Text.Contains('/D Y')) ('the message ' + $(if ($refusal.Text.Contains('/D Y')) { 'still prints "/D Y": this is a build from before the fix' } else { 'has no /D' }))
            }
            else {
                Add-Check 'the message carries the four recovery commands with the real path' $false 'there was no refusal message to read'
            }
            # The crashes the log recorded are the count that can be trusted: every start that reaches the
            # guard ends in one .NET Runtime 1026 event. The process ids are sampled only AFTER sc start
            # returns, so the first process is usually missed and a short-lived one can be too.
            $runtime = @($result.Events | Where-Object { $_.Log -eq 'Application' -and $_.Id -eq 1026 }).Count
            $scmDeaths = @($result.Events | Where-Object { $_.Log -eq 'System' -and ($_.Id -eq 7031 -or $_.Id -eq 7034) }).Count
            $starts = @($result.Pids).Count
            Add-Check 'INFO: recovery configured for this probe' $null $probe.Recovery
            Add-Check 'INFO: how many times the service started and died in about 42 seconds (1 = Windows did NOT restart it, more than 1 = it did)' $null ("Application log, .NET Runtime 1026 events: {0}; Service Control Manager 7031/7034 events: {1}" -f $runtime, $scmDeaths)
            Add-Check 'INFO: process ids sampled after sc start returned (the first process is usually not among them; a short-lived one can be missed)' $null ("distinct ids: {0}" -f $starts)
            Add-ScmInfo $result
        }
    },
    @{
        Name = 'deleteway'
        Description = 'The other way out: the planted folder is deleted from the elevated prompt. Expect: the service makes its own folder and token, and does not accept the planted one.'
        Run = {
            $dir = New-ScenarioDirectory 'deleteway'
            Set-OwnerTo $dir $script:Me
            Add-PlantedStore $dir $script:PlantedToken
            Set-OwnerTo (Join-Path $dir 'credentials.json') $script:Me
            $probe = New-Probe 'deleteway' $dir
            $first = Start-Probe $probe -WaitSeconds 12
            Add-Check 'refused first' ((-not $first.SawRunning) -and ($null -ne (Find-Event $first $script:RefusalNeedle))) ('final state: ' + $first.Final)
            $plain = $true
            try { Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction Stop } catch { $plain = $false }
            Add-Check 'a plain delete from the elevated prompt removed the folder (no takeown needed here)' ($plain -and ($null -eq (Get-PathAttributes $dir))) 'Remove-Item -Recurse -Force'
            if ($null -ne (Get-PathAttributes $dir)) { Remove-ProbePath $dir | Out-Null }
            $second = Start-Probe $probe -WaitSeconds 30
            $made = Get-PathFacts $dir
            $token = Get-StoreToken $dir
            Add-Check 'the service then starts and makes a protected folder of its own' ($second.SawRunning -and (Test-Safe $made)) ('final state: ' + $second.Final + '; ' + (Format-Facts $made))
            Add-Check 'with a NEW token: not the planted one' (($token.Length -gt 0) -and ($token -ne $script:PlantedToken)) 'compared in memory; nothing printed'
            $planted = Get-NetworkStatus $probe.Port $script:PlantedToken
            $own = Get-NetworkStatus $probe.Port $token
            Add-Check 'from the network the planted token is refused (401) and the new one accepted (200)' (($planted -eq 401) -and ($own -eq 200)) ("planted token: HTTP $planted; new token: HTTP $own")
        }
    },
    @{
        Name = 'opendacl'
        Description = 'A NON-EMPTY folder created by an ELEVATED account: owner Administrators, permissions inherited. Expect: refused (OpenDacl).'
        Run = {
            $dir = New-ScenarioDirectory 'opendacl'
            Set-OwnerTo $dir $script:AdminsSid
            Add-PlantedStore $dir $script:PlantedToken
            $before = Get-PathFacts $dir
            Assert-Precondition (($script:Trusted -contains $before.OwnerSid) -and -not $before.Protected -and ($before.Entries -contains 'credentials.json')) 'a trusted owner with permissions that inherit, and a file inside' (Format-Facts $before)
            $probe = New-Probe 'opendacl' $dir
            $result = Start-Probe $probe -WaitSeconds 12
            $after = Get-PathFacts $dir
            $refusal = Find-Event $result ($script:RefusalNeedle + ' (OpenDacl)')
            Add-NoStartCheck $result
            Add-EventCheck 'the verdict named in the message is OpenDacl' $refusal ($script:RefusalNeedle + ' (OpenDacl)')
            Add-Check 'the folder is left as it was' ((Get-Fingerprint $before) -eq (Get-Fingerprint $after)) (Format-Facts $after)
        }
    },
    @{
        Name = 'byhand'
        Description = 'What the service run BY HAND leaves: protected, but naming an extra account. Expect: refused; then the three recovery lines, then it starts.'
        Run = {
            $dir = New-ScenarioDirectory 'byhand'
            Set-OwnerTo $dir $script:AdminsSid
            $me = $script:Me
            $grant = Invoke-Native 'icacls.exe' @($dir, '/inheritance:r', '/grant:r', '*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F', "*${me}:(OI)(CI)F")
            Add-PlantedStore $dir $script:PlantedToken
            $before = Get-PathFacts $dir
            $extra = @($before.Sids | Where-Object { $script:Trusted -notcontains $_ })
            Assert-Precondition (($grant.Code -eq 0) -and $before.Protected -and ($before.OwnerSid -eq $script:AdminsSid) -and ($extra.Count -eq 1)) 'a protected folder owned by Administrators that names one extra account' ((Format-Facts $before) + '; icacls said: ' + $grant.Output)
            $probe = New-Probe 'byhand' $dir
            $first = Start-Probe $probe -WaitSeconds 12
            $refusal = Find-Event $first ($script:RefusalNeedle + ' (OpenDacl)')
            Add-NoStartCheck $first
            Add-EventCheck 'the verdict named in the message is OpenDacl' $refusal ($script:RefusalNeedle + ' (OpenDacl)')
            $codes = @()
            foreach ($command in (Get-RecoveryCommands $dir)) {
                $codes += (Invoke-Native $command[0] $command[1]).Code
            }
            $fixed = Get-PathFacts $dir
            $file = Get-PathFacts (Join-Path $dir 'credentials.json')
            $second = Start-Probe $probe -WaitSeconds 30
            Add-Check 'the three recovery lines all succeeded' (@($codes | Where-Object { $_ -ne 0 }).Count -eq 0) ('exit codes: ' + ($codes -join ', '))
            Add-Check 'the folder is now safe: trusted owner, protected, only SYSTEM and Administrators' (Test-Safe $fixed) (Format-Facts $fixed)
            Add-Check 'and so is the file inside: the extra account is gone from it too' (Test-SafeFile $file) (Format-Facts $file)
            Add-Check 'the service then starts' $second.SawRunning ('final state: ' + $second.Final)
            Add-Check 'and it adopted the file that was there: the planted token is accepted from the network (the documented consequence)' ((Get-NetworkStatus $probe.Port $script:PlantedToken) -eq 200) 'HTTP 200 for the token that was in the file'
        }
    },
    @{
        Name = 'recipe'
        Description = 'A hostile file in a hostile folder, then the three recovery lines exactly as printed. Expect: they work, /setowner included, and they fix the FILE too.'
        Run = {
            $dir = New-ScenarioDirectory 'recipe'
            Set-OwnerTo $dir $script:Me
            Add-PlantedStore $dir $script:PlantedToken
            $file = Join-Path $dir 'credentials.json'
            Set-OwnerTo $file $script:Me
            $craft = Invoke-Native 'icacls.exe' @($file, '/inheritance:r', '/grant:r', "*$($script:Me):F")
            $fileBefore = Get-PathFacts $file
            Assert-Precondition (($craft.Code -eq 0) -and ($fileBefore.OwnerSid -eq $script:Me) -and ($fileBefore.Sids.Count -eq 1) -and ($fileBefore.Sids[0] -eq $script:Me)) 'a file owned by an untrusted account whose permissions name only that account' ((Format-Facts $fileBefore) + '; icacls said: ' + $craft.Output)
            $probe = New-Probe 'recipe' $dir
            $first = Start-Probe $probe -WaitSeconds 12
            $refusal = Find-Event $first ($script:RefusalNeedle + ' (UntrustedOwner)')
            Add-NoStartCheck $first
            Add-EventCheck 'the verdict named in the message is UntrustedOwner' $refusal ($script:RefusalNeedle + ' (UntrustedOwner)')
            $codes = @()
            $outputs = @()
            foreach ($command in (Get-RecoveryCommands $dir)) {
                $result = Invoke-Native $command[0] $command[1]
                $codes += $result.Code
                $outputs += $result.Output
            }
            $fixed = Get-PathFacts $dir
            $fileAfter = Get-PathFacts $file
            $second = Start-Probe $probe -WaitSeconds 30
            Add-Check '/setowner to the Administrators group worked (it needs elevation; unmeasured before)' ($codes[0] -eq 0) ('exit ' + $codes[0] + ': ' + $outputs[0])
            Add-Check 'the three lines all succeeded' (@($codes | Where-Object { $_ -ne 0 }).Count -eq 0) ('exit codes: ' + ($codes -join ', '))
            Add-Check 'the folder is now safe' (Test-Safe $fixed) (Format-Facts $fixed)
            Add-Check 'the FILE is now safe too: owner SYSTEM or Administrators, and nobody else in its permissions' (Test-SafeFile $fileAfter) ('before: ' + (Format-Facts $fileBefore) + ' | after: ' + (Format-Facts $fileAfter))
            Add-Check 'the service starts' $second.SawRunning ('final state: ' + $second.Final)
            Add-Check 'and adopts what was there: the token in the file is accepted from the network' ((Get-NetworkStatus $probe.Port $script:PlantedToken) -eq 200) 'HTTP 200 for the token that was in the file'
        }
    },
    @{
        Name = 'adopt'
        Description = 'CONTROL: a file copied into a folder the service made itself. Expect: adopted as before, no refusal.'
        Run = {
            $dir = Join-Path $script:Root 'adopt'
            $probe = New-Probe 'adopt' $dir
            $first = Start-Probe $probe -WaitSeconds 30
            $made = Get-PathFacts $dir
            Add-Check 'the service made the folder itself (it did not exist), protected' ($first.SawRunning -and (Test-Safe $made)) (Format-Facts $made)
            Stop-Probe $probe
            Add-PlantedStore $dir $script:CopiedToken
            $second = Start-Probe $probe -WaitSeconds 30
            $refusal = Find-Event $second $script:RefusalNeedle
            Add-Check 'a file put in afterwards is adopted: the service starts, no refusal logged' ($second.SawRunning -and ($null -eq $refusal)) ('final state: ' + $second.Final)
            Add-Check 'and the file was not replaced: its token is accepted from the network' ((Get-NetworkStatus $probe.Port $script:CopiedToken) -eq 200) 'HTTP 200 for the token that was put in the file'
        }
    },
    @{
        Name = 'file'
        Description = 'A FILE where the folder should be (a standard user can create one in ProgramData). Expect: refused with a message that says so.'
        Run = {
            $dir = Join-Path $script:Root 'file'
            New-Item -ItemType File -Path $dir | Out-Null
            $probe = New-Probe 'file' $dir
            $result = Start-Probe $probe -WaitSeconds 12
            $refusal = Find-Event $result $script:CreateFailureNeedle
            Add-NoStartCheck $result
            Add-EventCheck 'the message says what is wrong: "could not create its credential directory"' $refusal $script:CreateFailureNeedle
            $after = Get-PathFacts $dir
            Add-Check 'the file is still a file, still there' ($after.Exists -and -not $after.IsDirectory) (Format-Facts $after)
        }
    },
    @{
        Name = 'takeown'
        Description = 'The takeown line the refusal prints, and the form it printed before. Not a service test: whether those commands run on THIS Windows.'
        Run = {
            $dir = New-ScenarioDirectory 'takeown'
            Add-PlantedStore $dir $script:PlantedToken
            # Through cmd with stdin from NUL: takeown asks a yes/no question when it meets something it
            # cannot open, and a question with nobody to answer would stop the whole run. (The folder is
            # the run folder's own and has no spaces in its path, so the line needs no quotes.)
            $printed = Invoke-Native 'cmd.exe' @('/c', ('takeown /F {0} /A /R < NUL' -f $dir))
            $old = Invoke-Native 'cmd.exe' @('/c', ('takeown /F {0} /A /R /D Y < NUL' -f $dir))
            Add-Check 'the takeown line the service prints now (no /D) runs on this Windows' ($printed.Code -eq 0) ('exit {0}: {1}' -f $printed.Code, ($printed.Output -replace '\s+', ' '))
            Add-Check 'INFO: the line it printed before, with /D Y (a non-zero exit is the defect the change fixed, on this language of Windows)' $null ('exit {0}: {1}' -f $old.Code, ($old.Output -replace '\s+', ' '))
        }
    }
)

# ---------------------------------------------------------------------------------------------
# Plan, cleanup, report
# ---------------------------------------------------------------------------------------------

function Show-Plan {
    Write-Host ''
    Write-Host 'Observer credential-directory probe' -ForegroundColor Cyan
    Write-Host ''
    Write-Host 'It will, from an elevated PowerShell:'
    Write-Host ('  1. create ONE folder, ' + $script:ProgramData + '\' + $script:Prefix + '<id>, protected (SYSTEM and Administrators only)')
    Write-Host '     by the very call that creates it, and check it before putting anything inside'
    Write-Host '  2. publish src/Observer.Service (or copy -ServiceDirectory, hashed before and after) into <id>\bin,'
    Write-Host '     and check nothing in it is a link or names an account other than SYSTEM and Administrators'
    Write-Host '  3. for each scenario below: build a folder in <id>, register a throwaway service running the'
    Write-Host '     real Observer.Service.exe as LocalSystem, start it, and record what happened'
    Write-Host '  4. remove every service and folder it created, list anything it could not, and compare the'
    Write-Host '     installed service Observer, its data folder and its program folder with how they were'
    Write-Host ''
    Write-Host 'It never touches the service Observer, C:\ProgramData\Observer or the installed program files.'
    Write-Host ('Names it creates start with "' + $script:Prefix + '". Each probe has its own port, its own pipe name and no')
    Write-Host 'history database. The tokens are random per run and are never printed.'
    Write-Host ''
    Write-Host 'Service command line, per probe:'
    Write-Host '  Observer.Service.exe --Observer:CredentialStorePath=<folder>\credentials.json'
    Write-Host '      --Observer:Network:HttpsPort=<free port> --Observer:LocalChannel:PipeName=<probe name>'
    Write-Host '      --Observer:Storage:Enabled=false --Observer:ApiToken='
    Write-Host '  (the empty ApiToken is deliberate: a token set in the machine environment or in an'
    Write-Host '  appsettings.Local.json would make the service skip the folder being measured)'
    Write-Host ''
    Write-Host 'Scenarios (about ten minutes in all):'
    foreach ($scenario in $script:Scenarios) {
        Write-Host ('  - {0,-12} {1}' -f $scenario.Name, $scenario.Description)
    }
    Write-Host ''
}

# The names of the probe services the Service Control Manager knows, asked of the SCM itself: by the
# NAME PATTERN in sc's output, because its labels are translated. $null - not an empty list - when it
# could not be asked, because "no services" and "could not ask" must never look alike to a caller that
# is about to delete the folder those services run from.
function Get-ProbeServiceNames {
    $query = Invoke-Native 'sc.exe' @('query', 'type=', 'service', 'state=', 'all')
    if ($query.Code -ne 0) { return $null }
    $pattern = [regex]::Escape($script:Prefix) + '[0-9a-f]{6}-[a-z]+'
    return , @([regex]::Matches($query.Output, $pattern) | ForEach-Object { $_.Value } | Sort-Object -Unique)
}

function Invoke-Cleanup {
    $names = Get-ProbeServiceNames
    if ($null -eq $names) {
        # Without the list there is no telling whether a service still points into a folder, and
        # removing the folder would leave it pointing at a path anyone can re-create.
        $script:Leftovers.Add('The list of services could not be read, so nothing was removed. Try again from an elevated PowerShell.')
        Write-Host 'NOT REMOVED:' -ForegroundColor Red
        foreach ($left in $script:Leftovers) { Write-Host ('  ' + $left) -ForegroundColor Red }
        return $false
    }
    $stuckRuns = New-Object Collections.Generic.HashSet[string]
    foreach ($name in $names) {
        Write-Host ('removing service ' + $name)
        if (-not (Remove-ProbeService $name)) {
            # <prefix><six hex digits> is the run folder this service was started from.
            $null = $stuckRuns.Add($name.Substring(0, $script:Prefix.Length + 6))
        }
    }
    $services = $names
    $roots = @(Get-ChildItem -LiteralPath $script:ProgramData -Force -Filter ($script:Prefix + '*') -ErrorAction SilentlyContinue)
    foreach ($item in $roots) {
        if (-not (Test-ProbeRootName $item.Name)) {
            Write-Host ('skipping ' + $item.FullName + ': not a name this script gives') -ForegroundColor Yellow
            continue
        }
        if ($stuckRuns.Contains($item.Name)) {
            $script:Leftovers.Add($item.FullName + ' was left in place on purpose: a service still points into it. Run this script with -Cleanup once that service is gone.')
            continue
        }
        $facts = Get-PathFacts $item.FullName
        # This script makes its run folder protected and owned by a trusted account. Anything else
        # with such a name is not its work, and it is not taken over with elevated tools.
        if ($facts.IsLink -or ($script:Trusted -notcontains $facts.OwnerSid)) {
            $script:Leftovers.Add(($item.FullName + ' was NOT touched: it is not what this script creates (' + (Format-Facts $facts) + '). Look at it, then remove it by hand.'))
            continue
        }
        Write-Host ('removing ' + $item.FullName)
        Remove-ProbePath $item.FullName | Out-Null
    }
    Write-Host ('looked at {0} service(s) and {1} folder(s) or file(s)' -f $services.Count, $roots.Count)
    if ($script:Leftovers.Count -gt 0) {
        Write-Host 'NOT REMOVED:' -ForegroundColor Red
        foreach ($left in $script:Leftovers) { Write-Host ('  ' + $left) -ForegroundColor Red }
        return $false
    }
    Write-Host 'done: nothing of the probe is left.' -ForegroundColor Green
    return $true
}

function Get-Mark {
    param($Pass)
    if ($Pass -eq $true) { return 'PASS' }
    if ($Pass -eq $false) { return 'FAIL' }
    return 'INFO'
}

# The report is BUILT here and WRITTEN elsewhere, so that a path that cannot be written to loses
# nothing: the caller can still print the lines.
function Get-ReportLines {
    param($Results, $Before, $After, $Differences, [string] $Build)
    $os = Get-CimInstance Win32_OperatingSystem
    $lines = New-Object Collections.Generic.List[string]
    $lines.Add('# Observer credential-directory probe')
    $lines.Add('')
    $lines.Add(('- Date: {0}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')))
    $lines.Add(('- Windows: {0} (build {1}), UI language {2}' -f $os.Caption, $os.BuildNumber, (Get-UICulture).Name))
    $lines.Add(('- PowerShell: {0} {1}, elevated: {2}, running as {3}' -f $PSVersionTable.PSEdition, $PSVersionTable.PSVersion, (Test-Elevated), [Security.Principal.WindowsIdentity]::GetCurrent().Name))
    $lines.Add(('- Service under test: {0}' -f $Build))
    $lines.Add(('- Run id: {0}' -f $script:Run))
    $lines.Add('')
    $lines.Add('| Scenario | Check | Result |')
    $lines.Add('|---|---|---|')
    foreach ($scenario in $Results) {
        foreach ($check in $scenario.Checks) {
            $lines.Add(('| {0} | {1} | {2} |' -f $scenario.Name, $check.Name.Replace('|', '/'), (Get-Mark $check.Pass)))
        }
    }
    $lines.Add('')
    $lines.Add('## Evidence')
    foreach ($scenario in $Results) {
        $lines.Add('')
        $lines.Add(('### {0}' -f $scenario.Name))
        $lines.Add(('_{0}_' -f $scenario.Description))
        $lines.Add('')
        $lines.Add('```')
        foreach ($check in $scenario.Checks) {
            $lines.Add(('[{0}] {1}' -f (Get-Mark $check.Pass), $check.Name))
            $lines.Add(('       {0}' -f $check.Evidence))
        }
        $lines.Add('```')
    }
    $lines.Add('')
    $lines.Add('## The installed Observer')
    $lines.Add('')
    if (-not $Before.Verified -or -not $After.Verified) {
        $lines.Add('NOT VERIFIED: a part of the installed Observer could not be read, before or after. See the parts below.')
    }
    elseif ($Before.Nothing) {
        $lines.Add('Nothing is installed here (no service, no data folder, no program folder), so there was nothing to protect and this check proves nothing.')
    }
    elseif ($Differences.Count -eq 0) {
        $lines.Add('Unchanged: the service configuration and process, and the data and program folders (permissions, names, sizes, times, short hashes), read the same before and after.')
    }
    else {
        $lines.Add('CHANGED while the probe ran: ' + ($Differences -join ', '))
    }
    $lines.Add('')
    $lines.Add('```')
    foreach ($key in $Before.Parts.Keys) {
        $same = 'same'
        if ($Before.Parts[$key] -ne $After.Parts[$key]) { $same = 'DIFFERENT' }
        $lines.Add(('{0}: {1}' -f $key, $same))
        if ($same -eq 'DIFFERENT' -or $Before.Parts[$key].StartsWith('UNREADABLE') -or $After.Parts[$key].StartsWith('UNREADABLE')) {
            # The short hashes stay out of the report: one of the files hashed is the installed
            # machine token, and a report is something people paste into a chat.
            $lines.Add(('  before: {0}' -f ($Before.Parts[$key] -replace 'sha256=[0-9A-Fa-f]{12}', 'sha256=(not shown)')))
            $lines.Add(('  after:  {0}' -f ($After.Parts[$key] -replace 'sha256=[0-9A-Fa-f]{12}', 'sha256=(not shown)')))
        }
    }
    $lines.Add('```')
    $lines.Add('')
    $lines.Add('## What was left behind')
    $lines.Add('')
    if ($script:Leftovers.Count -eq 0) {
        $lines.Add('Nothing of the probe''s own: every service and folder it made was removed and looked for again afterwards.')
    }
    else {
        foreach ($left in $script:Leftovers) { $lines.Add('- ' + $left) }
    }
    $lines.Add('')
    $lines.Add('Windows itself keeps traces the probe cannot remove: crash reports and Application log events (.NET Runtime 1026 and 1000) naming Observer.Service.exe, and possibly a few certificate key files in the key store of the account the services ran as. They come from the services that were made to fail on purpose.')
    return , $lines.ToArray()
}

# Written to a path that is checked NOW, not ten minutes ago: a link is refused, and the file is
# created exclusively (CreateNew fails if anything - a link included - has appeared at that name), so
# an elevated write never goes through something planted in between.
function Write-ReportFile {
    param([string[]] $Lines, [string] $Path)
    if (Test-IsLink $Path) { throw "The report path is a link: $Path" }
    [IO.File]::Delete($Path)
    $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes($Lines -join [Environment]::NewLine)
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length) }
    finally { $stream.Dispose() }
}

# ---------------------------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------------------------

if ($Cleanup) {
    if (-not (Test-Elevated)) { throw 'Run this from an ELEVATED PowerShell.' }
    if (-not (Invoke-Cleanup)) { exit 1 }
    return
}

Show-Plan

if ($Plan) { return }

# The arguments first: a wrong one is reported the same way whether or not the shell is elevated.
$known = @($script:Scenarios | ForEach-Object { $_.Name })
if ($Only) {
    $unknown = @($Only | Where-Object { $known -notcontains $_ })
    if ($unknown.Count -gt 0) { throw ('No such scenario: ' + ($unknown -join ', ') + '. They are: ' + ($known -join ', ')) }
}

$repository = Split-Path -Parent $PSScriptRoot
if (-not $ServiceDirectory) {
    if (-not (Test-Path -LiteralPath (Join-Path $repository 'src\Observer.Service\Observer.Service.csproj'))) {
        throw 'This script is not inside the repository, so it cannot publish the service: pass -ServiceDirectory. Nothing was changed.'
    }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'dotnet is not on the PATH, so the service cannot be published: pass -ServiceDirectory. Nothing was changed.'
    }
}
elseif (-not (Test-Path -LiteralPath (Join-Path $ServiceDirectory 'Observer.Service.exe'))) {
    throw "There is no Observer.Service.exe in $ServiceDirectory. Nothing was changed."
}
else {
    $ServiceDirectory = (Resolve-Path -LiteralPath $ServiceDirectory).ProviderPath
}

if (-not (Test-Elevated)) {
    throw 'Run this from an ELEVATED PowerShell (right-click, "Run as administrator"). Nothing was changed.'
}

if ($script:Trusted -contains $script:Me) {
    throw 'This runs as SYSTEM or as a service account the guard trusts, so no folder could be made to look hostile. Run it as your own administrator account. Nothing was changed.'
}

if ($script:ProgramData -match '[\s"]') {
    throw ('ProgramData is ' + $script:ProgramData + ', and the probe services take their paths without quoting. Nothing was changed.')
}

$leftBehind = @(Get-Service -Name ($script:Prefix + '*') -ErrorAction SilentlyContinue) + @(Get-ChildItem -LiteralPath $script:ProgramData -Force -Filter ($script:Prefix + '*') -ErrorAction SilentlyContinue)
if ($leftBehind.Count -gt 0) {
    throw 'A previous run left something behind (services or folders starting with ObserverProbe-). Run this script with -Cleanup first. Nothing was changed.'
}

# The report is written LAST, after ten minutes of work: a path that cannot be written to has to
# be found now. It is resolved against the PowerShell location (an elevated window starts in
# System32, and [IO.File] would use that), and this runs elevated, so it does not write through a link.
$ReportPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ReportPath)
if (Test-IsLink $ReportPath) {
    throw "The report path $ReportPath is a link, and this runs elevated: it will not write through one. Nothing was changed."
}
try {
    # Tried and, if it was not there before, taken away again: no empty file is left behind, and the
    # "Nothing was changed" of the refusals above stays true when the operator declines to go on. The
    # link check is repeated at the moment of writing, which creates the file exclusively.
    $reportExisted = $null -ne (Get-PathAttributes $ReportPath)
    [IO.File]::AppendAllText($ReportPath, '')
    if (-not $reportExisted) { [IO.File]::Delete($ReportPath) }
}
catch {
    throw "The report cannot be written to $ReportPath ($($_.Exception.Message)). Give -ReportPath a folder that exists. Nothing was changed."
}

if (-not $Yes) {
    $answer = Read-Host 'Type YES to run it'
    if ($answer -ne 'YES') { Write-Host 'Nothing was changed.'; return }
}

$script:PlantedToken = New-Token
$script:CopiedToken = New-Token
$script:Run = $script:Prefix + [guid]::NewGuid().ToString('N').Substring(0, 6)
$script:Root = Join-Path $script:ProgramData $script:Run
$bin = Join-Path $script:Root 'bin'
$script:ServiceExe = Join-Path $bin 'Observer.Service.exe'
$results = New-Object Collections.Generic.List[object]
Write-Host 'Reading the installed Observer, if there is one (before)...'
$installedBefore = Get-InstalledSnapshot
$build = 'unknown'
$rootMade = $false
$rootReowned = $false

try {
    Write-Host ('Run id: ' + $script:Run)

    New-ProtectedDirectory $script:Root $script:Trusted
    $rootMade = $true
    $made = Get-PathFacts $script:Root
    if ($script:Trusted -notcontains $made.OwnerSid) {
        # Some machines own what an administrator creates by the user account and not by the group.
        Invoke-Native 'icacls.exe' @($script:Root, '/setowner', ('*' + $script:AdminsSid)) | Out-Null
        $rootReowned = $true
        $made = Get-PathFacts $script:Root
    }
    if (-not (Test-Safe $made) -or $made.Entries.Count -ne 0) {
        throw ('The run folder is not what it must be, so nothing was put in it: ' + (Format-Facts $made))
    }

    Write-Host 'Preparing the service binaries...'
    if ($ServiceDirectory) {
        # A link in the source would make the copy carry whatever it points at, and the folder is
        # hashed before and after so that a file swapped WHILE it is copied is noticed. What happened
        # to it before this script started cannot be seen from here.
        $sourceLinks = Get-UnsafeEntries $ServiceDirectory
        if ($sourceLinks.Count -gt 0) { throw ('The service folder contains a link or something unlistable: ' + ($sourceLinks -join '; ')) }
        $sourceHash = Get-TreeHash $ServiceDirectory
        Copy-Item -LiteralPath $ServiceDirectory -Destination $bin -Recurse
        $copyHash = Get-TreeHash $bin
        if ($copyHash -ne $sourceHash) { throw ('The copy is not the same as the source (tree hash ' + $copyHash + ' against ' + $sourceHash + '): a file changed while it was copied.') }
        Write-Host ('  tree hash of the service folder, before and after the copy: ' + $sourceHash)
    }
    else {
        # --disable-build-servers: an elevated MSBuild or compiler server would stay behind, elevated,
        # after the run.
        $publish = Invoke-Native 'dotnet' @('publish', (Join-Path $repository 'src\Observer.Service'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--nologo', '--disable-build-servers', '-o', $bin)
        if ($publish.Code -ne 0) {
            $tail = (($publish.Output -split "`n") | Select-Object -Last 15) -join "`n"
            throw ('dotnet publish failed (exit ' + $publish.Code + '). The end of its output:' + "`n" + $tail)
        }
    }
    if (-not (Test-Path -LiteralPath $script:ServiceExe)) { throw "Observer.Service.exe is not in $bin." }
    if ($rootReowned) {
        # On this machine what the elevated session creates is owned by the user account, not by the
        # group: the binaries just made are the run folder's own doing, so they get the same owner the
        # run folder was given - after checking there is no link to follow.
        $binLinks = Get-UnsafeEntries $bin
        if ($binLinks.Count -gt 0) { throw ('The service binaries contain a link or something unlistable: ' + ($binLinks -join '; ')) }
        Invoke-Native 'icacls.exe' @($bin, '/setowner', ('*' + $script:AdminsSid), '/T', '/C') | Out-Null
    }
    # LocalSystem will run this. Every file in it must be owned by a trusted account and name nobody
    # else, and nothing in it may be a link, before a service points at it.
    $untrusted = Get-UntrustedInTree $bin
    if ($untrusted.Count -gt 0) {
        throw ('The service binaries are not safe to run as LocalSystem: ' + (($untrusted | Select-Object -First 5) -join '; '))
    }
    $version = (Get-Item -LiteralPath $script:ServiceExe).VersionInfo
    $build = ('{0}, product version {1}, sha256 {2}' -f $script:ServiceExe, $version.ProductVersion, (Get-Sha12 $script:ServiceExe))
    Write-Host ('Service under test: ' + $build)

    foreach ($scenario in $script:Scenarios) {
        if ($Only -and ($Only -notcontains $scenario.Name)) { continue }
        Write-Host ('  ' + $scenario.Name + ' ...') -NoNewline
        $script:Checks = New-Object Collections.Generic.List[object]
        try {
            & $scenario.Run
        }
        catch {
            Add-Check 'the scenario ran to the end' $false ('Aborted: ' + $_.Exception.Message)
        }
        finally {
            Stop-HeldProcesses
            Remove-ProbeService ($script:Run + '-' + $scenario.Name) | Out-Null
            Remove-ProbePath (Join-Path $script:Root $scenario.Name) | Out-Null
        }
        $checks = $script:Checks.ToArray()
        $failed = @($checks | Where-Object { $_.Pass -eq $false }).Count
        if ($failed -eq 0) { Write-Host ' ok' -ForegroundColor Green } else { Write-Host (' {0} FAILED' -f $failed) -ForegroundColor Red }
        $results.Add([pscustomobject]@{ Name = $scenario.Name; Description = $scenario.Description; Checks = $checks })
    }
}
finally {
    try {
        Stop-HeldProcesses
        if ($rootMade) {
            # The run folder holds the binaries every probe service points at. It is removed only when
            # every service is gone: a service left registered on a path that then stops existing is a
            # LocalSystem service any account may put a program at, in a folder they can create.
            $servicesGone = $true
            $names = Get-ProbeServiceNames
            if ($null -eq $names) {
                $servicesGone = $false
            }
            else {
                foreach ($name in @($names | Where-Object { $_.StartsWith($script:Run + '-') })) {
                    if (-not (Remove-ProbeService $name)) { $servicesGone = $false }
                }
            }
            if ($servicesGone) {
                Remove-ProbePath $script:Root | Out-Null
            }
            else {
                $script:Leftovers.Add($script:Root + ' was left in place on purpose: a service still points into it. Run this script with -Cleanup once that service is gone.')
            }
        }
    }
    catch {
        $script:Leftovers.Add('cleanup itself failed: ' + $_.Exception.Message + '. Run this script with -Cleanup.')
    }
    # Here and not after the report: a run that stops halfway still says what it left.
    if ($script:Leftovers.Count -gt 0) {
        Write-Host 'NOT REMOVED:' -ForegroundColor Red
        foreach ($left in $script:Leftovers) { Write-Host ('  ' + $left) -ForegroundColor Red }
        Write-Host 'Run this script with -Cleanup.' -ForegroundColor Red
    }
}

Write-Host 'Reading the installed Observer again (after)...'
$installedAfter = Get-InstalledSnapshot
$differences = Compare-Snapshots $installedBefore $installedAfter
$verified = $installedBefore.Verified -and $installedAfter.Verified
$untouched = $verified -and ($differences.Count -eq 0)
$reportLines = Get-ReportLines -Results $results.ToArray() -Before $installedBefore -After $installedAfter -Differences $differences -Build $build
$reportWritten = $true
try {
    Write-ReportFile -Lines $reportLines -Path $ReportPath
}
catch {
    # The results exist nowhere else: print them rather than lose ten minutes of measurements.
    $reportWritten = $false
    Write-Host ('The report could not be written to ' + $ReportPath + ': ' + $_.Exception.Message + '. Here it is:') -ForegroundColor Red
    foreach ($line in $reportLines) { Write-Host $line }
}

$allChecks = @($results | ForEach-Object { $_.Checks })
$failures = @($allChecks | Where-Object { $_.Pass -eq $false })
Write-Host ''
Write-Host ('{0} checks: {1} passed, {2} failed, {3} informational' -f $allChecks.Count, @($allChecks | Where-Object { $_.Pass -eq $true }).Count, $failures.Count, @($allChecks | Where-Object { $null -eq $_.Pass }).Count)
if (-not $verified) {
    Write-Host 'The installed Observer could NOT be verified (a part of it was unreadable). See the report.' -ForegroundColor Yellow
}
elseif ($installedBefore.Nothing) {
    Write-Host 'No Observer is installed here: the "untouched" check proves nothing.' -ForegroundColor Yellow
}
elseif ($untouched) {
    Write-Host 'The installed Observer service, data folder and program folder read the same as before.' -ForegroundColor Green
}
else {
    Write-Host ('WARNING: the installed Observer changed while the probe ran: ' + ($differences -join ', ') + '. See the report.') -ForegroundColor Red
}
if ($reportWritten) { Write-Host ('Report: ' + $ReportPath) }
if ($failures.Count -gt 0 -or -not $untouched -or $script:Leftovers.Count -gt 0 -or -not $reportWritten) { exit 1 }
