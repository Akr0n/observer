<#
.SYNOPSIS
    Checks the helpers of probe-credential-directory.ps1, without elevation and without touching
    any service.

.DESCRIPTION
    Loads everything of the probe script above its "Main" banner, so nothing runs, and exercises the
    helper functions against temporary folders: the permission and link checks, the removal guards, the
    report, the snapshot comparison, the held-open mechanism. It does NOT start the probe's services and
    cannot show what the real service does as LocalSystem - that is what the probe itself is for.

    Run it in BOTH shells after touching the probe (the two differ in ways that matter here):
        pwsh -NoProfile -File scripts/probe-credential-directory.tests.ps1
        powershell -NoProfile -ExecutionPolicy Bypass -File scripts/probe-credential-directory.tests.ps1
    Not run by CI. Unelevated is the intended way; elevated, the one test that needs a refused
    /setowner is skipped.
#>
param(
    [string] $Probe = (Join-Path $PSScriptRoot 'probe-credential-directory.ps1'),
    [string] $Scratch = ([IO.Path]::GetTempPath())
)

$ErrorActionPreference = 'Stop'

# Everything above the "Main" banner is definitions; nothing below is loaded, so nothing runs.
$text = [IO.File]::ReadAllText($Probe)
$cut = $text.IndexOf('# Main')
if ($cut -lt 0) { throw 'no Main marker' }
$definitions = $text.Substring(0, $cut)
$loaded = Join-Path $Scratch ('probe-definitions-only-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.ps1')
[IO.File]::WriteAllText($loaded, $definitions)
. $loaded
Remove-Item -LiteralPath $loaded -Force -ErrorAction SilentlyContinue

$script:Passed = 0
$script:Failed = 0

function Assert-That {
    param([string] $Name, $Condition, [string] $Detail = '')
    if ($Condition) { $script:Passed++; Write-Host "  ok    $Name" }
    else { $script:Failed++; Write-Host "  FAIL  $Name  $Detail" -ForegroundColor Red }
}

function Assert-Throws {
    param([string] $Name, [scriptblock] $Block, [string] $Like = '*')
    $message = $null
    try { & $Block } catch { $message = $_.Exception.Message }
    Assert-That $Name (($null -ne $message) -and ($message -like $Like)) ("message: $message")
}

$me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$elevated = Test-Elevated
$root = Join-Path ([IO.Path]::GetTempPath()) ('probe-test-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $root | Out-Null
$realProgramData = $script:ProgramData

try {
    Write-Host "-- shell: $($PSVersionTable.PSEdition) $($PSVersionTable.PSVersion), elevated: $elevated"

    # --- Get-PathFacts / Test-Safe / Test-SafeFile / Format-Facts -----------------------------
    $d = Join-Path $root 'plain'
    New-Item -ItemType Directory $d | Out-Null
    [IO.File]::WriteAllText((Join-Path $d 'credentials.json'), '{}')
    $f = Get-PathFacts $d
    Assert-That 'facts: exists and is a directory' ($f.Exists -and $f.IsDirectory -and -not $f.IsLink)
    Assert-That 'facts: owner is the running account (or Administrators when elevated)' (($f.OwnerSid -eq $me) -or ($f.OwnerSid -eq 'S-1-5-32-544')) $f.OwnerSid
    Assert-That 'facts: an inheriting folder is not protected' (-not $f.Protected)
    Assert-That 'facts: the entry is listed' ($f.Entries -contains 'credentials.json')
    Assert-That 'facts: permissions readable, listable' ($f.Readable -and $f.Listable)
    Assert-That 'safe: an inheriting folder is NOT safe' (-not (Test-Safe $f))

    $gone = Get-PathFacts (Join-Path $root 'nothing-here')
    Assert-That 'facts: a missing path says so' (-not $gone.Exists)
    Assert-That 'format: a missing path reads "absent"' ((Format-Facts $gone) -eq 'absent')
    Assert-That 'safe: a missing path is NOT safe' (-not (Test-Safe $gone))

    $fileFacts = Get-PathFacts (Join-Path $d 'credentials.json')
    Assert-That 'facts: a file is not a directory, has no entries' ($fileFacts.Exists -and -not $fileFacts.IsDirectory -and $fileFacts.Entries.Count -eq 0)

    # A link is a link: never read through.
    $target = Join-Path $root 'target'
    New-Item -ItemType Directory $target | Out-Null
    [IO.File]::WriteAllText((Join-Path $target 'inside.txt'), 'x')
    $link = Join-Path $root 'link'
    New-Item -ItemType Junction -Path $link -Target $target | Out-Null
    $lf = Get-PathFacts $link
    Assert-That 'facts: a junction is reported as a link' ($lf.Exists -and $lf.IsLink)
    Assert-That 'facts: a junction is not listed through (no entries, no owner read)' (($lf.Entries.Count -eq 0) -and ($lf.OwnerSid -eq ''))
    Assert-That 'safe: a junction is NOT safe' (-not (Test-Safe $lf))
    Assert-That 'format: a link reads as a link' ((Format-Facts $lf) -like '*link*')
    Assert-That 'link: Test-IsLink says yes for the junction, no for a plain folder, no for nothing' ((Test-IsLink $link) -and -not (Test-IsLink $d) -and -not (Test-IsLink (Join-Path $root 'nothing-here')))

    # Synthetic facts for the positive cases (a trusted owner cannot be built without elevation).
    $synthetic = [pscustomobject]@{ Exists = $true; IsDirectory = $true; IsLink = $false; Readable = $true; OwnerSid = 'S-1-5-32-544'; Protected = $true; Sids = @('S-1-5-18', 'S-1-5-32-544'); Entries = @(); ReadOnly = $false }
    Assert-That 'safe: trusted owner + protected + only SYSTEM/Administrators IS safe' (Test-Safe $synthetic)
    $open = [pscustomobject]@{ Exists = $true; IsDirectory = $true; IsLink = $false; Readable = $true; OwnerSid = 'S-1-5-32-544'; Protected = $false; Sids = @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-32-545'); Entries = @(); ReadOnly = $false }
    Assert-That 'safe: a trusted owner with an inheriting DACL is NOT safe' (-not (Test-Safe $open))
    $extra = [pscustomobject]@{ Exists = $true; IsDirectory = $true; IsLink = $false; Readable = $true; OwnerSid = 'S-1-5-32-544'; Protected = $true; Sids = @('S-1-5-18', 'S-1-5-32-544', $me); Entries = @(); ReadOnly = $false }
    Assert-That 'safe: protected but naming an extra account is NOT safe' (-not (Test-Safe $extra))
    $unreadable = [pscustomobject]@{ Exists = $true; IsDirectory = $true; IsLink = $false; Readable = $false; OwnerSid = ''; Protected = $false; Sids = @(); Entries = @(); ReadOnly = $false }
    Assert-That 'safe: unreadable permissions are NOT safe' (-not (Test-Safe $unreadable))
    $goodFile = [pscustomobject]@{ Exists = $true; IsDirectory = $false; IsLink = $false; Readable = $true; OwnerSid = 'S-1-5-18'; Protected = $false; Sids = @('S-1-5-18', 'S-1-5-32-544'); Entries = @(); ReadOnly = $false }
    Assert-That 'safe file: trusted owner, only SYSTEM/Administrators named IS safe (inheriting does not matter)' (Test-SafeFile $goodFile)
    $badFile = [pscustomobject]@{ Exists = $true; IsDirectory = $false; IsLink = $false; Readable = $true; OwnerSid = $me; Protected = $false; Sids = @('S-1-5-18', 'S-1-5-32-544'); Entries = @(); ReadOnly = $false }
    Assert-That 'safe file: an untrusted OWNER is NOT safe even with a perfect DACL' (-not (Test-SafeFile $badFile))
    $badFile2 = [pscustomobject]@{ Exists = $true; IsDirectory = $false; IsLink = $false; Readable = $true; OwnerSid = 'S-1-5-18'; Protected = $false; Sids = @('S-1-5-18', $me); Entries = @(); ReadOnly = $false }
    Assert-That 'safe file: an extra account in the permissions is NOT safe' (-not (Test-SafeFile $badFile2))

    # The exact shape a service run by hand leaves.
    $h = Join-Path $root 'byhand'
    New-Item -ItemType Directory $h | Out-Null
    $r = Invoke-Native 'icacls.exe' @($h, '/inheritance:r', '/grant:r', '*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F', "*${me}:(OI)(CI)F")
    $fh = Get-PathFacts $h
    Assert-That 'facts: a protected folder reads as protected, naming three accounts' ($fh.Protected -and $fh.Sids.Count -eq 3) (($fh.Sids -join ',') + ' icacls: ' + $r.Output)
    Assert-That 'safe: that shape is NOT safe' (-not (Test-Safe $fh))

    # Fingerprint: equal for the same folder, different once an entry appears.
    $fp1 = Get-Fingerprint (Get-PathFacts $d)
    [IO.File]::WriteAllText((Join-Path $d 'more.txt'), 'x')
    $fp2 = Get-Fingerprint (Get-PathFacts $d)
    Assert-That 'fingerprint: changes when an entry is added, stable otherwise' (($fp1 -ne $fp2) -and ($fp2 -eq (Get-Fingerprint (Get-PathFacts $d))))

    # --- New-ProtectedDirectory (the very call that creates the run folder) -------------------
    $pd = Join-Path $root 'protected'
    New-ProtectedDirectory $pd @($me)
    $pf = Get-PathFacts $pd
    Assert-That 'protected dir: created, protected, naming exactly the account given' ($pf.Exists -and $pf.IsDirectory -and $pf.Protected -and ($pf.Sids.Count -eq 1) -and ($pf.Sids[0] -eq $me)) (Format-Facts $pf)
    Assert-That 'protected dir: nothing inherited from the parent' ($pf.Sddl -notlike '*(A;OICIID;*')
    New-Item -ItemType File -Path (Join-Path $pd 'probe.txt') | Out-Null
    Assert-That 'protected dir: its own account can use it' (@(Get-ChildItem -LiteralPath $pd).Count -eq 1)
    Assert-Throws 'protected dir: refuses a path that already exists' { New-ProtectedDirectory $pd @($me) } '*Already exists*'
    # Files created inside inherit only what the folder names.
    $pfile = Get-PathFacts (Join-Path $pd 'probe.txt')
    Assert-That 'protected dir: a file made inside names only the account given' (($pfile.Sids.Count -eq 1) -and ($pfile.Sids[0] -eq $me)) ($pfile.Sids -join ',')

    # --- links in a tree ----------------------------------------------------------------------
    $tree = Join-Path $root 'tree'
    New-Item -ItemType Directory $tree | Out-Null
    New-Item -ItemType Directory (Join-Path $tree 'sub') | Out-Null
    [IO.File]::WriteAllText((Join-Path $tree 'sub\a.txt'), 'x')
    $empty = Get-UnsafeEntries $tree
    Assert-That 'unsafe entries: a clean tree gives an EMPTY array that .Count can be read from (StrictMode)' ($empty.Count -eq 0)
    New-Item -ItemType Junction -Path (Join-Path $tree 'sub\evil') -Target $target | Out-Null
    $bad = Get-UnsafeEntries $tree
    Assert-That 'unsafe entries: a junction deep in the tree is found' (($bad.Count -eq 1) -and ($bad[0] -like '*sub\evil')) ($bad -join ';')
    Assert-That 'unsafe entries: it did NOT descend into the junction' (-not (($bad -join ';') -like '*inside.txt*'))

    # --- untrusted-in-tree (the gate in front of running the binaries) ------------------------
    $bin = Join-Path $root 'bin'
    New-Item -ItemType Directory $bin | Out-Null
    [IO.File]::WriteAllText((Join-Path $bin 'Observer.Service.exe'), 'x')
    $problems = Get-UntrustedInTree $bin
    Assert-That 'untrusted in tree: files owned by an ordinary account are reported' ($problems.Count -ge 1 -and ($problems -join ';') -like '*Observer.Service.exe*') ($problems -join ';')
    $savedTrusted = $script:Trusted
    $script:Trusted = @('S-1-5-18', 'S-1-5-32-544', $me, 'S-1-5-32-545', 'S-1-1-0', 'S-1-5-11', 'S-1-5-32-544')
    $ownerOk = (Get-PathFacts (Join-Path $bin 'Observer.Service.exe')).Sids
    $script:Trusted = @('S-1-5-18', 'S-1-5-32-544') + @($ownerOk) + @($me)
    $none = Get-UntrustedInTree $bin
    Assert-That 'untrusted in tree: a tree whose owner and names are all trusted gives an EMPTY array (StrictMode .Count)' ($none.Count -eq 0) ($none -join ';')
    $script:Trusted = $savedTrusted
    $withLink = Get-UntrustedInTree $tree
    Assert-That 'untrusted in tree: a link anywhere makes the tree untrusted' (($withLink -join ';') -like '*link or unlistable*')

    # --- the probe area guard and the removal --------------------------------------------------
    $area = Join-Path $root 'area'
    New-Item -ItemType Directory $area | Out-Null
    $script:ProgramData = $area
    $ok = @(
        "$area\ObserverProbe-abcdef", "$area\ObserverProbe-abcdef\empty", "$area\ObserverProbe-0123ab\bin\x.dll"
    )
    $notOk = @(
        "$area\Observer", "$area\Observer\x", "$area\ObserverProbe-abcde", "$area\ObserverProbe-abcdefg",
        "$area\ObserverProbe-ABCDEF", "$area\ObserverProbe-abcdef\..\Observer", "$area\ObserverProbe-", "$area",
        'C:\Windows', "$area\ObserverProbe-abcdef-empty", '', "$area\other\ObserverProbe-abcdef"
    )
    foreach ($p in $ok) { Assert-That "area: accepts $p" (Test-InProbeArea $p) }
    foreach ($p in $notOk) { Assert-That "area: refuses '$p'" (-not (Test-InProbeArea $p)) }
    Assert-That 'root name: accepts ObserverProbe-abcdef, refuses others' ((Test-ProbeRootName 'ObserverProbe-abcdef') -and -not (Test-ProbeRootName 'ObserverProbe-abcdef-empty') -and -not (Test-ProbeRootName 'Observer'))
    Assert-Throws 'remove: refuses the INSTALLED folder' { Remove-ProbePath 'C:\ProgramData\Observer' } '*outside the probe area*'
    Assert-Throws 'remove: refuses anything outside the area' { Remove-ProbePath 'C:\Windows' } '*outside the probe area*'

    $script:Leftovers.Clear()
    $run = "$area\ObserverProbe-abcdef"
    New-Item -ItemType Directory $run | Out-Null
    New-Item -ItemType Directory "$run\ro" | Out-Null
    [IO.File]::WriteAllText("$run\ro\f.txt", 'x')
    Set-ReadOnlyFlag "$run\ro\f.txt"
    Set-ReadOnlyFlag "$run\ro"
    $gone1 = Remove-ProbePath "$run\ro"
    Assert-That 'remove: a read-only folder with a read-only file inside is removed' ($gone1 -and $null -eq (Get-PathAttributes "$run\ro")) ($script:Leftovers -join ';')
    New-Item -ItemType Directory "$run\lnk" | Out-Null
    New-Item -ItemType Junction -Path "$run\lnk\evil" -Target $target | Out-Null
    $gone2 = Remove-ProbePath "$run\lnk"
    Assert-That 'remove: a tree holding a link is LEFT and named, and the link target is untouched' ((-not $gone2) -and (Test-Path -LiteralPath (Join-Path $target 'inside.txt')) -and ($script:Leftovers.Count -eq 1)) ($script:Leftovers -join ';')
    [IO.Directory]::Delete("$run\lnk\evil", $false)
    New-Item -ItemType Junction -Path "$run\toplink" -Target $target | Out-Null
    $gone3 = Remove-ProbePath "$run\toplink"
    Assert-That 'remove: a link at the top is removed as a link, never through it' ($gone3 -and (Test-Path -LiteralPath (Join-Path $target 'inside.txt')) -and $null -eq (Get-PathAttributes "$run\toplink"))
    Assert-That 'remove: a path that is not there is a quiet success' (Remove-ProbePath "$run\never-existed")
    $script:Leftovers.Clear()
    $script:ProgramData = $realProgramData

    # --- the witness stream and the held-open process -----------------------------------------
    $w = Join-Path $root 'witness'
    New-Item -ItemType Directory $w | Out-Null
    Add-Witness $w
    Assert-That 'witness: present after being added, and the folder still counts as EMPTY' ((Test-Witness $w) -and ((Get-PathFacts $w).Entries.Count -eq 0))
    Remove-Item -LiteralPath $w -Force
    New-Item -ItemType Directory $w | Out-Null
    Assert-That 'witness: gone once the folder is replaced' (-not (Test-Witness $w))

    $hd = Join-Path $root 'held'
    New-Item -ItemType Directory $hd | Out-Null
    $holder = Start-HeldProcess $hd
    $deleteFailed = $false
    try { [IO.Directory]::Delete($hd) } catch { $deleteFailed = $true }
    Assert-That 'held: a process with the folder as its current directory makes Directory.Delete FAIL' ($deleteFailed -and (Test-Path -LiteralPath $hd)) "exited: $($holder.HasExited)"
    Stop-HeldProcesses
    Assert-That 'held: the process is gone after Stop-HeldProcesses, and the list is empty' ((-not (Get-Process -Id $holder.Id -ErrorAction SilentlyContinue)) -and $script:HeldProcesses.Count -eq 0)
    $deleteWorks = $true
    try { [IO.Directory]::Delete($hd) } catch { $deleteWorks = $false }
    Assert-That 'held: and the folder can be deleted afterwards' ($deleteWorks -and -not (Test-Path -LiteralPath $hd))

    # --- Set-OwnerTo: a gate, not a wish --------------------------------------------------------
    if (-not $elevated) {
        $so = Join-Path $root 'setowner'
        New-Item -ItemType Directory $so | Out-Null
        Assert-Throws 'owner: an owner the caller cannot assign is a failed PRECONDITION (unelevated)' { Set-OwnerTo $so 'S-1-5-32-544' } '*PRECONDITION NOT MET*'
        Set-OwnerTo $so $me
        Assert-That 'owner: assigning the caller''s own account works' ((Get-PathFacts $so).OwnerSid -eq $me)
    }
    Assert-Throws 'precondition: a false condition throws with the words PRECONDITION NOT MET' { Assert-Precondition $false 'a thing' 'evidence' } '*PRECONDITION NOT MET*a thing*evidence*'
    Assert-That 'precondition: a true condition is silent' ($null -eq (Assert-Precondition $true 'a thing' 'evidence'))

    # --- planted store, tokens ----------------------------------------------------------------
    $p = Join-Path $root 'plant'
    New-Item -ItemType Directory $p | Out-Null
    Add-PlantedStore $p 'tok'
    $json = Read-Store $p
    Assert-That 'planted store: valid JSON in the shape the service reads' (($json | ConvertFrom-Json).current -eq 'tok') $json
    Assert-That 'planted store: property names are the camelCase the service uses' ($json -like '*"previousExpiresAt":null*')
    Assert-That 'read-store: a missing store reads empty' ((Read-Store (Join-Path $root 'nothing-here')) -eq '')
    Assert-That 'store token: read back' ((Get-StoreToken $p) -eq 'tok')
    [IO.File]::WriteAllText((Join-Path $p 'credentials.json'), 'not json')
    Assert-That 'store token: garbage reads as empty, not as an exception' ((Get-StoreToken $p) -eq '')
    $t1 = New-Token; $t2 = New-Token
    Assert-That 'tokens: random, distinct, long enough to be unguessable, prefixed' (($t1 -ne $t2) -and $t1.StartsWith('probe-') -and $t1.Length -ge 60)

    # --- Invoke-Native ------------------------------------------------------------------------
    $n = Invoke-Native 'cmd.exe' @('/c', 'exit 3')
    Assert-That 'native: exit code is data, not an exception' ($n.Code -eq 3) "code=$($n.Code)"
    $n2 = Invoke-Native 'cmd.exe' @('/c', 'echo hello 1>&2')
    Assert-That 'native: stderr is captured and does not throw under Stop' ($n2.Output -like '*hello*') $n2.Output

    # --- ports and the network probe ----------------------------------------------------------
    $port = Get-FreePort
    Assert-That 'port: a usable port number' (($port -gt 1023) -and ($port -lt 65536)) "$port"
    Assert-That 'network: curl.exe is where the probe expects it' (Test-Path -LiteralPath (Join-Path $env:SystemRoot 'System32\curl.exe'))
    $closed = Get-NetworkStatus (Get-FreePort) 'x' 1
    Assert-That 'network: nothing listening reads as 0 within the wait' ($closed -eq 0) "$closed"

    # --- services ------------------------------------------------------------------------------
    Assert-That 'service sample: a service that is not there reads Gone' ((Get-ServiceSample 'ObserverProbe-nothere-none').Status -eq 'Gone')
    $spooler = Get-ServiceSample 'Spooler'
    Assert-That 'service sample: a real service reads a real state' (@('Running', 'Stopped', 'Start Pending', 'Stop Pending', 'Paused') -contains $spooler.Status) $spooler.Status
    $script:Run = 'ObserverProbe-abc123'
    foreach ($scenario in $script:Scenarios) {
        $name = $script:Run + '-' + $scenario.Name
        $threw = $false
        try { Remove-ProbeService $name | Out-Null } catch { $threw = $true }
        Assert-That "service name: '$name' passes the guard that must accept every scenario's service" (-not $threw)
    }
    Assert-Throws 'service guard: refuses the INSTALLED service named Observer' { Remove-ProbeService 'Observer' } '*not a probe*'
    Assert-Throws 'service guard: refuses a service that is not a probe' { Remove-ProbeService 'Spooler' } '*not a probe*'
    Assert-Throws 'service guard: refuses a probe-looking name with the wrong shape' { Remove-ProbeService 'ObserverProbe-abc123' } '*not a probe*'
    $ev = Get-ProbeEvents -Since (Get-Date).AddSeconds(-5) -Service 'ObserverProbe-nothere-none' -Folder 'C:\nope'
    Assert-That 'events: no matches gives an EMPTY array that .Count can be read from (StrictMode)' ($ev.Count -eq 0)

    # --- the scenario table ---------------------------------------------------------------------
    $names = @($script:Scenarios | ForEach-Object { $_.Name })
    Assert-That 'scenarios: names are unique, lower-case letters only (they end up in service names)' ((@($names | Select-Object -Unique).Count -eq $names.Count) -and (@($names | Where-Object { $_ -notmatch '^[a-z]+$' }).Count -eq 0)) ($names -join ',')
    Assert-That 'scenarios: every one has a description and a script block' (@($script:Scenarios | Where-Object { -not $_.Description -or $_.Run -isnot [scriptblock] }).Count -eq 0)

    # --- recovery commands and the message check ----------------------------------------------
    $dir = 'C:\ProgramData\ObserverProbe-abc123\planted'
    $commands = @(Get-RecoveryCommands $dir)
    Assert-That 'recovery: three commands, each a file and an argument list' ($commands.Count -eq 3 -and $commands[0].Count -eq 2 -and $commands[0][0] -eq 'icacls.exe')
    Assert-That 'recovery: the first is /setowner to the Administrators GROUP, recursive' (($commands[0][1] -contains '/setowner') -and ($commands[0][1] -contains '*S-1-5-32-544') -and ($commands[0][1] -contains '/T'))
    Assert-That 'recovery: the second is /reset /T' (($commands[1][1] -contains '/reset') -and ($commands[1][1] -contains '/T'))
    Assert-That 'recovery: the third removes inheritance and grants only SYSTEM and Administrators' (($commands[2][1] -contains '/inheritance:r') -and ($commands[2][1] -contains '*S-1-5-18:(OI)(CI)F') -and ($commands[2][1] -contains '*S-1-5-32-544:(OI)(CI)F'))
    foreach ($command in (Get-RecoveryCommands $dir)) {
        Assert-That 'recovery: foreach hands over one [file, arguments] pair at a time' (($command[0] -is [string]) -and ($command[1] -is [array]))
    }

    $good = "The credential directory '$dir' is not empty ... run:`n  icacls `"$dir`" /setowner `"*S-1-5-32-544`" /T`n  icacls `"$dir`" /reset /T`n  icacls `"$dir`" /inheritance:r /grant:r `"*S-1-5-18:(OI)(CI)F`" `"*S-1-5-32-544:(OI)(CI)F`"`n(if Windows refuses, first run: takeown /F `"$dir`" /A /R /D Y)"
    $nothingMissing = Get-MissingRecoveryText $good $dir
    Assert-That 'message check: a complete message misses nothing, and .Count works on the empty result (StrictMode)' ($nothingMissing.Count -eq 0)
    Assert-That 'message check: the takeown typo is caught' ((Get-MissingRecoveryText $good.Replace('takeown', 'takeout') $dir).Count -eq 1)
    Assert-That 'message check: a missing asterisk is caught (it is literal, not a wildcard)' ((Get-MissingRecoveryText $good.Replace('"*S-1-5-32-544" /T', '"S-1-5-32-544" /T') $dir).Count -ge 1)
    Assert-That 'message check: a message with a DIFFERENT path is caught' ((Get-MissingRecoveryText $good 'C:\ProgramData\Other').Count -eq 4)

    # --- events and checks ----------------------------------------------------------------------
    $script:Checks = New-Object Collections.Generic.List[object]
    $fakeResult = [pscustomobject]@{
        Events = @(
            [pscustomobject]@{ Log = 'Application'; Id = 1026; Provider = '.NET Runtime'; Text = "Exception: The credential directory 'X' is not empty, and as it stands nothing vouches for what is in it (UntrustedOwner): its owner is not" },
            [pscustomobject]@{ Log = 'System'; Id = 7034; Provider = 'Service Control Manager'; Text = 'The ObserverProbe-abc123-planted service terminated unexpectedly.' }
        )
        SawRunning = $false; Final = 'Stopped'; ExitCode = 1067; StartOutput = '[SC] StartService FAILED 1067'; Pids = @(101, 202)
    }
    Assert-That 'find event: the whole phrase with the verdict is found' ($null -ne (Find-Event $fakeResult ($script:RefusalNeedle + ' (UntrustedOwner)')))
    Assert-That 'find event: a different verdict is NOT found' ($null -eq (Find-Event $fakeResult ($script:RefusalNeedle + ' (OpenDacl)')))
    Assert-That 'find event: the replace-failure phrase is not confused with the refusal' ($null -eq (Find-Event $fakeResult $script:ReplaceFailureNeedle))
    Assert-That 'find event: only the Application log counts' ($null -eq (Find-Event $fakeResult 'terminated unexpectedly'))
    Assert-That 'find event: no events at all is fine' ($null -eq (Find-Event ([pscustomobject]@{ Events = @() }) 'x'))
    Add-EventCheck 'named' (Find-Event $fakeResult $script:RefusalNeedle) $script:RefusalNeedle
    Add-EventCheck 'missing' $null 'x'
    Add-NoStartCheck $fakeResult
    Add-ScmInfo $fakeResult
    Add-ScmInfo ([pscustomobject]@{ Events = @() })
    $checks = $script:Checks.ToArray()
    Assert-That 'checks: event check passes with the excerpt in its evidence' (($checks[0].Pass -eq $true) -and ($checks[0].Evidence -like '*.NET Runtime*UntrustedOwner*')) $checks[0].Evidence
    Assert-That 'checks: a missing event is a FAIL with a reason' (($checks[1].Pass -eq $false) -and ($checks[1].Evidence -like '*no such event*'))
    Assert-That 'checks: no-start check passes for a service that never ran, and says the exit code' (($checks[2].Pass -eq $true) -and ($checks[2].Evidence -like '*1067*'))
    Assert-That 'checks: SCM info is INFO (null), with the event id' (($null -eq $checks[3].Pass) -and ($checks[3].Evidence -like '*7034*'))
    Assert-That 'checks: SCM info with no events is INFO too' (($null -eq $checks[4].Pass) -and ($checks[4].Evidence -like '0 event*'))
    Assert-That 'excerpt: centred on the needle' ((Get-Excerpt 'aaa needle bbb' 'needle' 2 6) -eq 'a needle')
    Assert-That 'excerpt: needle absent gives the start of the text' ((Get-Excerpt 'hello world' 'zzz' 5 5) -eq 'hello')
    Assert-That 'excerpt: empty text is empty' ((Get-Excerpt '' 'x') -eq '')
    Assert-That 'excerpt: newlines collapse to single spaces' ((Get-Excerpt "a`r`n  b" 'a' 0 10) -eq 'a b')

    # --- marks and the report -----------------------------------------------------------------
    Assert-That 'mark: true/false/null' (((Get-Mark $true) -eq 'PASS') -and ((Get-Mark $false) -eq 'FAIL') -and ((Get-Mark $null) -eq 'INFO'))

    $script:Run = 'ObserverProbe-test00'
    $script:Leftovers.Clear()
    $results = @(
        [pscustomobject]@{ Name = 'one'; Description = 'first'; Checks = @([pscustomobject]@{ Name = 'a | pipe in a name'; Pass = $true; Evidence = 'ev1' }, [pscustomobject]@{ Name = 'b'; Pass = $false; Evidence = 'ev2' }, [pscustomobject]@{ Name = 'c'; Pass = $null; Evidence = 'ev3' }) }
    )
    $snapA = [pscustomobject]@{ Parts = ([ordered]@{ 'service' = 'state=Running'; 'data folder' = 'absent' }); Verified = $true; Nothing = $false }
    $snapSame = [pscustomobject]@{ Parts = ([ordered]@{ 'service' = 'state=Running'; 'data folder' = 'absent' }); Verified = $true; Nothing = $false }
    $snapDiff = [pscustomobject]@{ Parts = ([ordered]@{ 'service' = 'state=Stopped'; 'data folder' = 'absent' }); Verified = $true; Nothing = $false }
    $snapBad = [pscustomobject]@{ Parts = ([ordered]@{ 'service' = 'UNREADABLE: boom'; 'data folder' = 'absent' }); Verified = $false; Nothing = $false }
    $snapNothing = [pscustomobject]@{ Parts = ([ordered]@{ 'service' = 'not installed'; 'data folder' = 'absent' }); Verified = $true; Nothing = $true }

    $same = Compare-Snapshots $snapA $snapSame
    Assert-That 'compare: identical snapshots give an EMPTY array that .Count can be read from (StrictMode)' ($same.Count -eq 0)
    $diff = Compare-Snapshots $snapA $snapDiff
    Assert-That 'compare: the differing part is named' (($diff.Count -eq 1) -and ($diff[0] -eq 'service'))

    $out = Join-Path $root 'report.md'
    $md = (Get-ReportLines -Results $results -Before $snapA -After $snapSame -Differences $same -Build 'exe, product version 9.9, sha256 abc') -join "`n"
    Assert-That 'report: the table has one row per check, a pipe in a name is escaped' (($md -like '*| one | a / pipe in a name | PASS |*') -and ($md -like '*| one | b | FAIL |*') -and ($md -like '*| one | c | INFO |*'))
    Assert-That 'report: carries the evidence, the build under test, the run id' (($md -like '*ev2*') -and ($md -like '*Service under test: exe, product version 9.9*') -and ($md -like '*ObserverProbe-test00*'))
    Assert-That 'report: unchanged installation is said as such' ($md -like '*Unchanged:*')
    Assert-That 'report: nothing left behind is said as such' ($md -like '*Nothing: every service and folder*')
    $md = (Get-ReportLines -Results $results -Before $snapA -After $snapDiff -Differences $diff -Build 'x') -join "`n"
    Assert-That 'report: a changed installation is CHANGED, with the before/after of the part' (($md -like '*CHANGED while the probe ran: service*') -and ($md -like '*before: state=Running*') -and ($md -like '*after:  state=Stopped*'))
    $md = (Get-ReportLines -Results $results -Before $snapA -After $snapBad -Differences (Compare-Snapshots $snapA $snapBad) -Build 'x') -join "`n"
    Assert-That 'report: an unreadable snapshot is NOT VERIFIED, never "unchanged"' (($md -like '*NOT VERIFIED*') -and ($md -notlike '*Unchanged:*'))
    $md = (Get-ReportLines -Results $results -Before $snapNothing -After $snapNothing -Differences (Compare-Snapshots $snapNothing $snapNothing) -Build 'x') -join "`n"
    Assert-That 'report: nothing installed says the check proves nothing' ($md -like '*proves nothing*')
    $script:Leftovers.Add('C:\somewhere could not be removed: because')
    $md = (Get-ReportLines -Results $results -Before $snapA -After $snapSame -Differences $same -Build 'x') -join "`n"
    Assert-That 'report: leftovers are listed by name and reason' ($md -like '*- C:\somewhere could not be removed: because*')
    $script:Leftovers.Clear()

    # --- the tree snapshot and the hash ---------------------------------------------------------
    $ts = Join-Path $root 'snap'
    New-Item -ItemType Directory $ts | Out-Null
    [IO.File]::WriteAllText((Join-Path $ts 'a.bin'), 'abc')
    Assert-That 'hash: SHA-256 of "abc", first 12 hex digits' ((Get-Sha12 (Join-Path $ts 'a.bin')) -eq 'BA7816BF8F01')
    $s1 = Get-TreeSnapshot $ts
    Assert-That 'tree snapshot: stable when nothing changes' ($s1 -eq (Get-TreeSnapshot $ts))
    [IO.File]::WriteAllText((Join-Path $ts 'a.bin'), 'abd')
    Assert-That 'tree snapshot: changes when a file changes' ($s1 -ne (Get-TreeSnapshot $ts))
    Assert-That 'tree snapshot: a missing folder reads "absent"' ((Get-TreeSnapshot (Join-Path $root 'nothing-here')) -eq 'absent')
    $lockStream = New-Object IO.FileStream((Join-Path $ts 'a.bin'), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::ReadWrite)
    try { Assert-That 'hash: a file another process holds open for writing can still be read' ((Get-Sha12 (Join-Path $ts 'a.bin')).Length -eq 12) }
    finally { $lockStream.Dispose() }

    # --- the installed snapshot on THIS machine -------------------------------------------------
    $snapshot = Get-InstalledSnapshot
    Assert-That 'installed snapshot: never throws, has the three parts, a Verified and a Nothing flag' (($snapshot.Parts.Keys -contains 'service') -and ($snapshot.Parts.Keys -contains 'data folder') -and ($snapshot.Parts.Keys -contains 'program folder') -and ($snapshot.Verified -is [bool]) -and ($snapshot.Nothing -is [bool])) (($snapshot.Parts | Out-String))
    Assert-That 'installed snapshot: reading twice gives the same answer (nothing volatile in it)' ((Compare-Snapshots $snapshot (Get-InstalledSnapshot)).Count -eq 0)

    # --- what the review found -------------------------------------------------------------------
    $areaRun = Join-Path $area 'ObserverProbe-abcdef'
    $script:ProgramData = $area
    $script:Leftovers.Clear()
    New-Item -ItemType File -Path "$areaRun\afile" | Out-Null
    Set-ReadOnlyFlag "$areaRun\afile"
    $fileGone = Remove-ProbePath "$areaRun\afile"
    Assert-That 'remove: a plain (read-only) FILE is removed, and is not reported as a leftover' ($fileGone -and $null -eq (Get-PathAttributes "$areaRun\afile") -and $script:Leftovers.Count -eq 0) ($script:Leftovers -join ';')
    $script:Leftovers.Add("$areaRun\stuck could not be removed: it was held")
    $script:Leftovers.Add('service ObserverProbe-abcdef-stuck is still registered (x)')
    New-Item -ItemType Directory "$areaRun\stuck" | Out-Null
    $null = Remove-ProbePath "$areaRun\stuck"
    Assert-That 'remove: an earlier leftover entry for a path that is now gone is withdrawn, others stay' (($script:Leftovers.Count -eq 1) -and ($script:Leftovers[0] -like 'service *')) ($script:Leftovers -join ';')
    $script:Leftovers.Clear()
    $script:ProgramData = $realProgramData

    Assert-That 'hash: full SHA-256 is 64 hex digits, and the short one is its start' (((Get-FileSha256 (Join-Path $ts 'a.bin')).Length -eq 64) -and ((Get-FileSha256 (Join-Path $ts 'a.bin')).StartsWith((Get-Sha12 (Join-Path $ts 'a.bin')))))
    $th = Join-Path $root 'treehash'
    New-Item -ItemType Directory "$th\sub" | Out-Null
    [IO.File]::WriteAllText("$th\one.dll", 'aaa')
    [IO.File]::WriteAllText("$th\sub\two.dll", 'bbb')
    $h1 = Get-TreeHash $th
    Assert-That 'tree hash: same folder, same hash' ($h1 -eq (Get-TreeHash $th) -and $h1.Length -eq 16)
    [IO.File]::WriteAllText("$th\sub\two.dll", 'bbc')
    $h2 = Get-TreeHash $th
    Assert-That 'tree hash: one changed byte changes it' ($h1 -ne $h2)
    Rename-Item -LiteralPath "$th\one.dll" -NewName 'uno.dll'
    Assert-That 'tree hash: a renamed file changes it' ($h2 -ne (Get-TreeHash $th))
    $copy = Join-Path $root 'treehash-copy'
    Copy-Item -LiteralPath $th -Destination $copy -Recurse
    Assert-That 'tree hash: a copy of the folder has the same hash (what the run relies on)' ((Get-TreeHash $th) -eq (Get-TreeHash $copy))

    $pf = Get-NativeProgramFiles
    Assert-That 'program files: the native folder, and it exists' ((Test-Path -LiteralPath $pf) -and ($pf -notlike '*(x86)*'))
    $e = Invoke-Native 'cmd.exe' @('/c', 'echo boom 1>&2')
    Assert-That 'native: a stderr line comes back as plain text, without error-record decoration' ($e.Output -eq 'boom') ("[" + $e.Output + "]")

    $maskA = [pscustomobject]@{ Parts = ([ordered]@{ 'data folder' = 'x credentials.json length=1 sha256=0123456789AB' }); Verified = $true; Nothing = $false }
    $maskB = [pscustomobject]@{ Parts = ([ordered]@{ 'data folder' = 'x credentials.json length=1 sha256=BA9876543210' }); Verified = $true; Nothing = $false }
    $mdMask = (Get-ReportLines -Results $results -Before $maskA -After $maskB -Differences (Compare-Snapshots $maskA $maskB) -Build 'x') -join "`n"
    Assert-That 'report: a differing part is shown WITHOUT the file hashes' (($mdMask -like '*CHANGED*') -and ($mdMask -notlike '*0123456789AB*') -and ($mdMask -notlike '*BA9876543210*') -and ($mdMask -like '*sha256=(not shown)*'))

    # --- the plan -------------------------------------------------------------------------------
    $planText = (Show-Plan 6>&1 | Out-String)
    Assert-That 'plan: names every scenario' (@($script:Scenarios | Where-Object { $planText -notlike ('*' + $_.Name + '*') }).Count -eq 0)
    Assert-That 'plan: says what it never touches' ($planText -like '*never touches the service Observer*')
}
finally {
    $script:ProgramData = $realProgramData
    Stop-HeldProcesses
    # Links first, by the probe's own non-following walk: Remove-Item on a junction throws in Windows
    # PowerShell 5.1, and a recursive delete through one would reach its target.
    foreach ($x in @($root)) {
        foreach ($l in (Get-UnsafeEntries $x)) { try { [IO.Directory]::Delete(($l -replace ' \(cannot be listed.*$', ''), $false) } catch { } }
        & icacls.exe $x /grant "*${me}:(OI)(CI)F" /T /C 2>&1 | Out-Null
        Remove-Item -LiteralPath $x -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ''
Write-Host ("{0} passed, {1} failed" -f $script:Passed, $script:Failed)
if ($script:Failed -gt 0) { exit 1 }
