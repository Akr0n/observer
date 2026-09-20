# Observer

![build](https://github.com/Akr0n/observer/actions/workflows/build.yml/badge.svg)

Cross-platform dashboard for monitoring the vital signs of this machine
and of the devices on the local network. Runs on Windows and Linux.

> **Status:** working and installable. The service samples once a second on Windows and on
> Linux - CPU, memory, space per volume, activity per disk - keeps the series in SQLite,
> generates its own machine token, and exposes the data both on the network and on a local
> channel that needs no credentials. The desktop client shows them live, with the history
> strip under the gauges - an hour, a day or a week - and the CPU gauge, the memory gauge or
> a disk's activity gauge opens the list of the processes consuming that resource, from which
> a process can be ended. There is an MSI package for Windows and a `.deb` for Linux,
> which register the service and install the dashboard. Network monitoring and temperature
> sensors are still missing.

## Architecture

The project is split into a headless service and a desktop client: on Windows, services
run in Session 0 and cannot show a graphical interface, so collection and display must be
two separate processes.

| Project | Role |
| --- | --- |
| `src/Observer.Core` | Shared models, the collector abstraction and platform adapters |
| `src/Observer.Service` | Headless service: samples at 1 Hz, keeps the series in SQLite with aggregation, and exposes the data over authenticated HTTP |
| `src/Observer.App` | Avalonia desktop client: connects to the service and shows the metrics live |
| `src/Observer.Cli` | The `observer` command line: shares the key, rotates it, runs diagnostics, and keeps the other machines' tokens |
| `tests/Observer.Core.Tests` | Tests for `Observer.Core` |
| `tests/Observer.Service.Tests` | Tests for `Observer.Service`, history and local channel included |
| `tests/Observer.App.Tests` | Tests for the client: the HTTP client and the mapping of the responses, the view models and their status rules, preferences, and the gauge layout |
| `tests/Observer.Cli.Tests` | Tests for the command line's messages |

The client can point at the service running on the same machine or on another one.

### What it measures

| Metric | One instance is | Notes |
| --- | --- | --- |
| CPU | the machine | usage percentage, from the delta of the system times |
| Memory | the machine | used, available and total; "available" is an estimate when the system does not expose it directly, and it says so |
| Disk space | a volume (`C:`, `/`) | used, free and total; a capacity of zero is "unknown", not "empty" |
| Disk activity | a device (`Disk 0`, `sda`) | bytes read and written per second, and percentage of busy time |

Disk-activity instances are **devices**, not volumes, and deliberately do not line up with the
space ones: a disk carries several volumes and a volume can span several disks, so attributing
the traffic of two volumes to one drive letter would be worse than an honest device name. The
busy percentage is derived from **idle** time, never by adding read time and write time: the
two overlap, and on one measured window the sum came to 843%.

Besides the metrics, the service exposes the **process list** sorted by CPU, by memory or by
I/O, and that is what the dashboard opens when you click the matching gauge: the window
scrolls down to the list by itself, a second click on the same gauge closes it, and a click on
another gauge switches it. A disk's activity gauge opens the I/O list, which covers the
**whole machine**, and its title says so: the counters are per process, and neither system
says which device the bytes ended up on. The space gauges open nothing: the space used on a
volume cannot be attributed to a running process.

The window also remembers **which machine you were watching** and reopens on it: anyone
keeping an eye on a computer on the network no longer has to pick it every time the window
opens. What is saved is the name, never the address and certainly not the token, and it is
the same name that is in `machines.json`; if that entry is gone, the window falls back to
this computer, silently.

The window **remembers where it was**, how big it was and whether it was maximised, and
reopens there - unless that place is on a screen that is no longer there, in which case it
opens wherever the system puts it. Top right is the **zoom** (75, 85, 100, 115, 130, 150 %):
it scales the whole window, gauges included, and it is remembered too. Above 100 % the steps
are the ones Windows uses for text; below it you see more without scrolling - more rows of
history and, in a window at the default size, one more column of gauges - at the price of
smaller text. 75 % is the floor, and a measured one: buttons go from 32 to 24 px and the
status ring keeps its hole. Next to it is the **theme**: System (follows the system, as
before), Light or Dark, which takes effect at once, drop-down lists included and, on
Windows 11, the title bar too. These settings, together with the machine you were watching,
live in `preferences.json` next to `client.json` - a separate file, because `client.json` can
contain a credential and must not be rewritten every time the window closes. The gauges, the
history strip and the selected row take the PC's accent colour, read at start-up: on Windows
they also follow a change made while the window is open; on Linux the accent is read only on
KDE, LXQt and LXDE, and elsewhere it stays the theme's default violet.

"I/O" means every read and write the process asked for, cache included: it is the only
per-process counter Windows has, and on Linux the service reads `rchar` and `wchar` - not
`read_bytes` and `write_bytes`, which would be truer to the disk but different from what the
other machine says under the same title. Also, on Linux, `/proc/PID/io` can be read only with
*ptrace* permission on that process: the service runs as the `observer` user and shows a dash
for other users' processes. This is deliberate - `CAP_SYS_PTRACE` would allow reading the
memory of any process - and anyone who wants to lift the restriction adds
`AmbientCapabilities=CAP_SYS_PTRACE` to the systemd unit, knowing what that grants.

### Adding a metric

The only extension point is `IMetricCollector`. Each collector publishes its own
`MetricDescriptor`s and returns a list of `MetricPoint`s, in a format that is the same for
every source. The per-instance dimension — the core, the disk, the network interface —
is a string field on the point, not a type hierarchy: that is why per-disk and
per-process sources can go through the same interface without changing it. Units of
measure are an open type, so a sensor in `rpm` or in `V` does not require touching the Core.

In practice you write one new class, but there are five files to touch, and it is worth
knowing that beforehand:

| file | why |
|---|---|
| `src/Observer.Core/Metrics/<Name>/<Name>Collector.cs` | the collector |
| `src/Observer.Core/Composition/ObserverMetrics.cs` | the registration, and which provider on which system |
| `src/Observer.Core/Platform/HostPlatform.cs` | the `Unsupported` provider, for a system where it cannot be measured |
| `src/Observer.Core/Platform/Windows/WindowsProviders.cs` | how it is measured on Windows |
| `src/Observer.Core/Platform/Linux/LinuxProviders.cs` | how it is measured on Linux |

Plus the table of human-readable titles in `src/Observer.App/Services/SnapshotProjection.cs`,
without which the tile is titled `disk` instead of `Disks`.

What must **not** be touched is the interface: `IMetricCollector` handles a new source
unchanged, and the two lines that matter - the per-instance dimension as a field of the point
and the unit as an open type - are what make that true.

A metric that cannot be measured on a platform **stays in the catalog** and is reported as
`Unsupported`, with the reason, instead of disappearing: "it cannot be measured here" and "I
forgot it" must stay distinguishable in the dashboard.

The same holds **for each individual instance**. A point can be built only through the
factories `MetricPoint.Measured`, `.Unsupported` or `.Unavailable`, and carries its own state
and its own reason. This is needed for the ordinary case of a multi-instance source: a source with
three disks, one of them behind a USB bridge that does not forward SMART commands, must be
able to report the two healthy disks **and** the explanation for the third. A collector that
reads several instances must therefore emit one point for each, failed ones included —
leaving an instance out means "not applicable", not "I failed".

### The sampling constraint

**Only one `BackgroundService` samples.** The HTTP endpoints read the latest snapshot from
the cache and never call `CollectAsync`. This is not a performance choice: the CPU
collector keeps the previous sample, and two simultaneous collections would produce
percentages that are intermittently wrong and look plausible.

### History and rollup

The service keeps the series in SQLite at three levels of detail: the raw sample at
1 s, the 1-minute aggregate and the 5-minute aggregate. Without aggregation the file would
grow without bound.

Each bucket keeps the **sum and the count**, not the average. When buckets with different
numbers of samples are recombined — the normal case after a restart or a collector timeout —
the average of the averages would give a number that is believable and false.

The defaults, all of which can be changed in `appsettings.json` under `Observer:Storage`:

| Parameter | Default | What it covers |
| --- | --- | --- |
| `RawRetention` | 6 hours | the per-second detail |
| `MinuteRetention` | 7 days | "this time last week" |
| `FiveMinuteRetention` | 90 days | the long-term trend |
| `Enabled` | `true` | when `false`, the service behaves as if the history did not exist |

Data is never deleted before it has been aggregated, even when retention would allow it. A
missing point stays missing and never becomes a zero: in a chart a zero is a value, a gap is
a gap.

### Endpoints

An **identified local** caller reaches all of them **without any token**: on the machine,
the operating system already knows who is calling, and a shared secret would be the wrong
tool. From the **network** the bearer token remains mandatory.

| Endpoint | What it returns |
| --- | --- |
| `GET /metrics/catalog` | the metrics that exist, with a readable name and unit |
| `GET /metrics/latest` | the latest sample, or `503` when there is none to give: the service has not sampled yet, or it has **stopped** sampling |
| `GET /metrics/series` | which series have actually been measured on this machine |
| `GET /metrics/history` | the historical points; `resolution` accepts `auto`, `raw`, `1m`, `5m` |
| `GET /metrics/storage` | where it writes, how much space it takes up, how far it has aggregated |
| `GET /processes` | the processes using the most; `by` accepts `cpu` (default), `memory` or `io`, `top` from 1 to 100 (default 15); the response echoes the criterion applied in `by` |
| `POST /processes/{pid}/kill` | terminates that process, and `name` is **required**: `204` if it worked, `400` if the request did not name its target, `404` if the pid does not exist, `409` if that pid is now a different process, `403` if the caller may not stop processes here or the operating system protects that one — the body says which |
| `POST /credentials/reload` | **local channel only** — makes the service re-read its credential store, and answers with when the file it read had last been written. From the network it does not exist: `404`, even with a valid token |

`auto` picks the finest resolution still available for the requested interval: yesterday's
raw data has been deleted, and returning an empty chart would read as "machine not
monitored".

**A sample that has stopped advancing is not served.** `/metrics/latest` reads from a cache that
the sampler fills, and never samples itself — two requests at once would skew the CPU
arithmetic. The price of that is a sampling loop which dies while the rest of the service keeps
answering: the cache goes on handing out the same snapshot, and a `200` says "this is the
machine right now". So that endpoint answers `503` once its newest reading is more than fifteen
seconds old, with a sentence saying which of the two silences it is. Fifteen seconds is fifteen
missed rounds; a single slow round is normal under load and the service logs those separately.
The age is measured on the service's own monotonic counter, never on the reading's timestamp, so
a clock that is wrong or gets corrected changes nothing.

`/processes` is **not** affected: it is served from its own live reading of the process table, so
on a machine whose sampler has died the process list and the kill still work — which is what you
want, because that list is how you find out what wedged it.

An older dashboard against this service still gets the protection: a `503` reaches it as "the
service is not sampling", and after ten seconds it raises a warning like any other — it just
calls the fault "no readings yet" instead of "not measuring", and cannot tell the two apart. The
other way round gets nothing: **a dashboard newer than its service** talks to a service with no
such check, which answers `200` with the stale reading exactly as before. Update both sides
together.

`/processes/{pid}/kill` is the service's **only write**, and it is allowed from the network
with the token, by deliberate choice: from another machine you see a runaway process and
stop it from there. Every attempt ends up in the service log with the pid and where the caller
came from; the process name is there too whenever there was one to read, which means on the kill
that worked, on the one the operating system refused, and on the one refused because that pid
had become a different process — that last line carries both names.

**A pid on its own is not accepted.** It is a number the system reuses, and the one the caller
holds came from a list that is at least a second old — longer if somebody stopped to think
before confirming. So the request must carry `?name=` with the name that list showed, and the
service compares it with the live process before killing anything: a mismatch is refused with
`409` and nothing is stopped. A request that names nothing is refused with `400` rather than
carried out on whatever holds the number now, which means a dashboard older than the service
stops being able to kill — loudly, and that is the intent. The other way round nothing warns
you: a dashboard newer than the service sends a name the old service ignores, and the answer is
the same `204` either way. **Update the service and the dashboard together.**

What the check buys is worth being exact about: it refuses a pid that has become a *different*
program, not one that has become another copy of the *same* program — every instance of Chrome
is called Chrome, and the short-lived processes that free pids fastest are exactly the ones that
come in copies.

**On Windows, the local channel requires an elevated caller.** The pipe admits INTERACTIVE —
every user with a session on that machine — deliberately, so the person at the console can watch
the gauges without being put in a group. Watching is not stopping: the service runs as
LocalSystem, so without this an ordinary interactive user could end any process on the machine.
The question asked is what the caller's own **token** can do, not what its account is: a member
of Administrators who has not elevated holds a token from which Windows removed the group, and
honouring the account instead would hand back exactly the privilege it removed. In practice, to
end a process on this machine you start the dashboard as an administrator; the gauges, the
history and the process list need nothing. Note that this has to be *your own* account
elevating: if you are not an administrator, "Run as administrator" starts the dashboard as
somebody else, with somebody else's profile — so it would look for `machines.json` and the
stored tokens under that account and find neither. **On Linux there is no such check and none is
needed**: the socket is `0660` inside a `0750` directory, both owned by the service's user and
group, so the kernel has already turned away anyone outside that group. **From the network
nothing changes** — there is no identity to read there, only the token, and refusing would
remove the reason the kill is reachable from another machine at all. The kill
endpoint is also why the token is no longer kept in a file
(see "Watching another machine"). `GET /processes` returns `503` when the list cannot be
read on that machine.

**What a caller may cost, before the token is even looked at.** A connection is accepted, and a
request body received, by whoever can reach the port — the `401` comes after. So three limits sit
on the near side of that check, before it rather than after, and this is a service whose whole job
is to watch a machine: turning it into the machine's problem is the one failure it must not have.
It accepts at most **512 connections per endpoint**, which is a budget each listener gets
separately — a flood on the port the other machines use therefore does not spend the local
channel's budget, and cannot lock you out of the machine you are sitting at. It does not make the
local channel itself unfloodable: that has a budget of its own, and on Windows the pipe admits
every interactive user by design, so a standard user on the machine can fill it. A refused
connection carries no status — the dashboard shows "Service unreachable", the same as a cable
fault, and the only other sign is one line in the service's log. It reads **no
request body**: no endpoint reads one, the kill takes its arguments in the URL precisely so that it
does not need one, and a caller that sends one gets its connection torn down instead of the body
drained — the limit fires on the read, not on the announcement, so a body is not refused at the
door. And it speaks **HTTP/1.1 only** — on the HTTPS endpoint HTTP/2 is not merely unused, it is
not offered in the TLS handshake, so a second protocol implementation with its own framing and its
own header compression is not reachable by an unauthenticated caller. On the local channel that
setting changes nothing and is kept only as a guard: a cleartext endpoint refuses HTTP/2 anyway,
which was measured rather than assumed. The numbers, and what was measured to choose them, are in
`src/Observer.Service/ServiceLimits.cs`.

## Requirements

- .NET SDK 10.0
- Windows 10/11 or a Linux distribution with a graphical environment

## Development

```bash
dotnet build
```

```bash
dotnet test
```

**Nothing needs configuring.** The service listens over HTTPS on `0.0.0.0:5058` and also opens a
local channel — a named pipe on Windows, a unix socket on Linux — on which an identified
local caller is served without credentials. As for the machine token, which is needed only so
that ANOTHER computer can query this one, the service generates it for itself on first start
and keeps it under `C:\ProgramData\Observer` or `/etc/observer`, with permissions that
exclude every other account.

Starting the service and the client, in two separate terminals:

```bash
dotnet run --project src/Observer.Service
```

```bash
dotnet run --project src/Observer.App
```

The dashboard does not need to know anything: with no configuration it uses the local channel
of the machine it runs on.

To read the metrics **over the network**, on the other hand, you need that machine's token,
which you get on that machine, from an elevated terminal:

```bash
observer share
```

`observer share` prints **two** values, and both are needed: the token says the caller is
authorised, the certificate fingerprint says the machine is who it claims to be. Without the
second, anyone who manages to put themselves in the middle presents their own certificate
and the token is handed straight to them.

The normal way to use them is the dashboard: the address and the fingerprint go in
`machines.json`, the token goes into this machine's credential store with `observer token set`
(see "Watching another machine"), and the dashboard compares the fingerprint itself. From the
command line, `curl` has no authority to rely on, because the certificate is self-signed: the
fingerprint has to be compared **by hand**, and only then do you go ahead.

```bash
# 1. which fingerprint that machine presents, as seen from here
openssl s_client -connect that-machine:5058 </dev/null 2>/dev/null   | openssl x509 -noout -fingerprint -sha256

# 2. if and ONLY if it matches the one printed by "observer share" on that machine:
curl --insecure -H "Authorization: Bearer $Observer__ApiToken"   https://that-machine:5058/metrics/latest
```

`--insecure` turns off every check, so it must never be used on its own: here it is
acceptable because step 1 has already done, by hand, the check that matters.

### Command line

After installing with the MSI, `observer` is already in the system `PATH`: just open a
**new** terminal. Without installing, the executable has to be invoked by its path, and in
PowerShell that needs the call operator `&` - to PowerShell, a quoted path at the start of a
line is a string, not a command, and the obvious attempt fails with a syntax error that does
not name its own cause.

| Verb | Elevated | What it does |
| --- | --- | --- |
| `observer share` | yes | shows the machine token and the fingerprint, to configure ANOTHER computer |
| `observer rotate-key` | yes | generates a new key; the previous one stays valid for another 24 hours, and the service uses the old one until it is restarted |
| `observer rotate-key --now` | yes | for a key that has **leaked**: no previous key is kept, and the running service is told to adopt the new store immediately |
| `observer doctor` | no | where the credential store is, how it is protected, and whether the local channel answers |

**`--now` exists because rewriting the file revokes nothing on its own.** The service reads its
store once, when it starts, so the plain `rotate-key` leaves the old key being accepted until
somebody restarts it — which is fine for a key you are merely tired of, and useless for one that
has just leaked. `--now` therefore does two more things: it writes no previous key at all, so the
compromised secret is not left on disk, and it asks the running service, through the local
channel, to adopt the new store there and then.

It reports whether that worked rather than assuming it. The service answers with **when the file
it read had last been written**, and the command compares that with the stamp of the file it just
wrote: equal means the running process read those exact bytes, which "the service said OK" would
not. It exits `0` only when the old key is provably accepted nowhere on this machine — including
the case where nothing is running here at all, which is the one other way of being sure. Every
other outcome is named and exits `1`: a service too old to have the endpoint, a service that read
a different store, one that was given its token in configuration and never had a store to adopt,
and the dangerous one — a silent local channel with something still listening on the port, which
means a service is running and still holds the old key. That last case is why the command also
probes the port: the two causes of a silent local channel mean opposite things.

Once it has applied, every computer that watches this one is cut off until you run
`observer token set NAME` there with the new token.
| `observer token set NAME` | no | keeps the token of ANOTHER machine; it reads it from standard input and does not show it |
| `observer token forget NAME` | no | forgets that token |

### Watching another machine

The machine you are sitting at needs nothing: the dashboard connects through the local channel,
with no port and no token. To watch another one you need **two** values, and they do different
jobs.

```bash
observer share
```

run on **that** machine, from an elevated terminal, prints the token and the fingerprint of its
certificate. The token says the caller is allowed in; the fingerprint says the machine is the
one it claims to be.

The fingerprint and the address go in `machines.json`, next to `client.json`. **The token does
not:**

```json
{
  "machines": [
    {
      "name": "laptop",
      "baseAddress": "https://laptop:5058/",
      "fingerprint": "sha256:..."
    }
  ]
}
```

`name` is **required**: it is the key the token is kept under, and it is checked before any
path is built from it - letters, digits, space, `.`, `_` and `-`, nothing else - because a name
like `../../id_rsa` would otherwise read and overwrite a file outside the folder.

The token is handed to this machine with a command, and you do not write it anywhere:

```bash
observer token set laptop
```

The command reads it from standard input and does not show it while you type. It ends up in the
**Windows Credential Manager**, or — on Linux — in a file readable only by its owner, which
Observer refuses to use if the permissions are any wider. The dashboard reads `machines.json`
when it starts, so reopen it once the entry and the token are in place.

The reason for this changed recently, and it is worth spelling out: since
`/processes/{pid}/kill` was added, that token no longer just lets you **read** another
machine's CPU: it also lets you **stop processes** on it. A file meant to be opened, copied and
pasted is not the place for a credential like that, which is why an entry that still carries one
is refused — even when the token is the right one.

**Upgrading from a version before 0.6.0**: for each remote machine run
`observer token set NAME` and then delete the `apiToken` line from `machines.json`. As long as
the line is there, that machine appears under the list as unusable, with the command to run
written next to it.

Next to every machine in the list there is a **status mark** that tells you whether it answers,
without having to click on it: a hollow grey ring until it has been heard from, a filled green
dot if it answers, an amber ring while a fault has lasted less than ten seconds, and a red
diamond after that, except for a service that answers but has not produced a reading yet, which
stays an amber ring. Filled or hollow, circle or diamond: it can be read even by someone who
cannot tell the colours apart, because in the light theme amber and red are the same colour for
the most common form of colour blindness. The colours are the status bar's, so a red mark means
what a red bar means. Under the name, when there is a fault to report, you can also read **how
long it has lasted** - "for 3 min", "for 2 h 10 min" - and the same phrase is in the tooltip; a
rejected token or an incompatible version is red at once. The machines you are not watching are
probed every fifteen seconds, all together and without holding up the gauges: a machine that is
switched off costs an eight-second timeout, and the gauges must not pay for it. The tooltip on
the row gives the reason.

The **sidebar is always there**, even when there is only one machine: that is where you find
the exact path of the `machines.json` you need to write. Hiding it until there are two machines
would mean announcing the feature only to those who already know it exists.

The local machine is always the first: you do not list it, and it cannot be removed. A malformed
entry **does not vanish silently** - it appears under the list with the reason, because a
machine that is simply not there cannot be told apart from one that was never added.

When a machine does not answer, the status bar distinguishes **connection refused** - something
is at that address but the service is not listening: it needs to be started - from **no answer**
within 8 seconds, which is usually a closed port or a firewall. The two remedies are opposite,
and mixing them up costs an afternoon. For the first 10 seconds the bar stays neutral, not red:
a service that is still starting refuses connections too. A rejected token, a different
fingerprint or a service older than the dashboard are red at once, because a minute later they
will be exactly the same. That message can be **copied**, with the `Copy` button on the bar
itself: a fingerprint mismatch prints both fingerprints in full, and nobody retypes those by hand.
A row of the process list can be copied too, from the right-click menu, and it carries the PID
with it - which identifies the process when there are twelve called "chrome". The rest of the
text is deliberately not selectable: selectable text takes the click to start a selection, and
dragging over a gauge would open its panel instead of selecting the words.

**On the network the service answers only over HTTPS.** It used to answer in cleartext, and the
token crossed the network once a second: a single packet capture handed over a permanent
credential, and rotating it did not help, because the new one was on the wire a second later.
The certificate is self-signed and generated by the service itself, so no authority vouches for
it: **it is the fingerprint that ties the connection to that machine**, and that is why you get
nowhere without it. If it ever changes, the dashboard stops and shows the old one and the new
one. After a reinstall that is normal, and you update the file; if you have not reinstalled
anything, do not copy the new value.

### Packages

```bash
./packaging/windows/pack.ps1
```

```bash
./packaging/linux/pack.sh
```

The first produces an MSI, the second a `.deb`. They register the service, install the
dashboard and create the menu shortcut.

**The port in the firewall.** The MSI adds two rules to Windows Firewall for port `5058/tcp`,
bound to the service's executable: one for **private** networks and one for **domain**
networks, both limited to the **local subnet**. Never on public networks: a café's Wi-Fi is no
place to expose a machine's metrics, not even behind a token. Uninstalling removes the rules.
If the remote machine is on another subnet, the rule has to be widened by hand, knowing what
that grants. Up to 0.7.0 the service listened on the network and the firewall silently refused
every connection: the remote dashboard said "no answer" and sent you looking for a network
fault that did not exist.

The `.deb`, on the other hand, **opens nothing**, because a Debian package does not touch the
firewall of whoever installs it: it ships a profile for `ufw`, so that

```bash
sudo ufw allow Observer
```

is all it takes, and the note at the end of the installation says so. In neither case is this
needed to watch the machine you are sitting at: the dashboard connects through the local
channel.

**If the service dies, it restarts by itself.** The MSI sets Windows' recovery actions: a
restart after five seconds, on the first failure and on every later one, with the count reset
after a day without failures. On Linux the systemd unit does it; it has had
`Restart=on-failure` since the first version. Before 0.14.1 the Windows side had none, and a
process that died left the service stopped until the machine was restarted, and nothing
reported it.

**Uninstalling the MSI from Control Panel removes everything**: the service, the files, the
credential store under `ProgramData` and the history, which lives in the system account's
profile, a place nobody would go looking in by hand. An **upgrade** is excluded from
this clean-up, and the distinction is not a fine point: Windows uninstalls the previous version
before installing the new one, so without that condition every upgrade would take the token and
the certificate away - and with a new fingerprint every remote dashboard would stop, showing a
message about someone in the middle of the connection. An upgrade must not look like an attack.
**Neither package knows any token**: the service obtains one for itself on first start, so there
is no secret to pass to the installation, to record in a log, or to leave behind if it fails
halfway.

The `.deb` also installs `man observer` and `man observer-dashboard`, and it is checked by
**lintian** in CI: the `pack-linux` job runs it with `--fail-on error,warning` on the package it
has just built. The only overridden tag is `embedded-library` - `libSkiaSharp.so` carries
`freetype`, `libjpeg` and `libpng` compiled in, and no variant linked against the system
libraries exists. The reason is written down in `packaging/linux/debian/lintian-overrides`,
because a vulnerability in one of those three is not closed by updating Debian.

### A note on globalization

`InvariantGlobalization` does **not** go in `Directory.Build.props`: set there, it has been
verified to silently switch off the CA1305 and CA1310 analyzers, which are precisely the ones
that prevent culture-dependent parsing of `/proc`. Invariant globalization at runtime is
guaranteed by the `runtimeconfig.template.json` files, which every **executable** project must
have.

## Code signing policy

The packages published by this project are **not code signed**. Windows will say so
twice, in two different ways, and the two are not fixed by the same thing:

- User Account Control will show **"Unknown publisher"**. A signature removes this
  immediately, from the first download.
- **SmartScreen** will warn on first run of the installer. A signature does *not*
  remove this: since March 2024 not even an EV certificate bypasses it. It depends on
  how many clean downloads the file has accumulated, not on the certificate it carries.

What is available today instead: every released package carries a **GitHub build
provenance attestation**, which ties it to the commit and the workflow that produced
it. Windows does not look at it, but it answers a different and equally useful
question — *does this file really come from that source code*:

```bash
gh attestation verify Observer.msi --repo Akr0n/observer
```

**Roles.** Committers, reviewers and approvers: Federico Cardinali
([@Akr0n](https://github.com/Akr0n)). Every change reaches `main` through a pull
request; direct pushes are refused by a repository ruleset. Release packages are built
only by GitHub-hosted runners, from a tag, by
[`release.yml`](.github/workflows/release.yml), which refuses to publish when the
version inside a package disagrees with the tag.

**Privacy.** This program will not transfer any information to other networked systems
unless specifically requested by the user or the person installing or operating it. The
service exposes measurements over HTTP on request and makes no outbound connections of
its own; the dashboard connects only to the addresses the user writes into its own
configuration file. There is no telemetry, no usage reporting and no automatic update
check.

## License

[MIT](LICENSE)