<#
.SYNOPSIS
    Costruisce il pacchetto Debian e lo installa in un container, per provare il percorso
    remoto della dashboard senza avere un secondo computer.

.DESCRIPTION
    Il percorso remoto di Observer - pacchetto, servizio, HTTPS, token, impronta, elenco
    delle macchine - e' l'unica parte che nessun test puo' esercitare da sola, perche' per
    definizione ha bisogno di due macchine. Questo script ne fabbrica la seconda con podman.

    Cosa questa prova dimostra: che il percorso funziona per intero, dal .deb fino alla
    riga nella barra laterale.

    Cosa NON dimostra: che i numeri siano giusti su hardware diverso. Il container
    condivide il kernel della macchina virtuale di podman, quindi la CPU e la memoria che
    vedrai sono quelle della VM, non di un computer separato.

.PARAMETER CartellaDiLavoro
    Dove finisce il .deb costruito. Viene creata se non esiste.

.PARAMETER Nome
    Il nome del container. Con lo stesso nome si riprende la prova di ieri.

.PARAMETER Smonta
    Rimuove il container e finisce. Attenzione: token e impronta se ne vanno con lui.

.EXAMPLE
    .\scripts\prova-macchina-esterna.ps1

.EXAMPLE
    .\scripts\prova-macchina-esterna.ps1 -Smonta

.NOTES
    Tre cose misurate il 2026-08-28, che spiegano perche' lo script e' fatto cosi'.

    1. Il .deb si costruisce PER FORZA dentro un container: su Windows mancano dpkg-deb
       e strip, e il repository montato si presenta a 0777, quindi i chmod di pack.sh non
       terrebbero. Il repository si monta in sola lettura e si copia dentro con tar, cosi'
       la compilazione non riusa gli obj costruiti su Windows.

    2. Dall'host Windows il container si raggiunge su "localhost", NON su "127.0.0.1",
       che rifiuta la connessione. Funziona perche' Kestrel ascolta anche in IPv6
       (https://[::]:5058) e il forwarder di podman inoltra da [::1]. Un processo che
       ascoltasse solo in IPv4 non sarebbe raggiungibile da localhost in nessun modo.
       NON usare l'indirizzo IP della macchina virtuale: cambia a ogni riavvio di WSL, e
       la dashboard diventerebbe rossa un giorno qualunque senza una ragione visibile.

    3. Il servizio va avviato A MANO dopo l'installazione, e non e' un difetto: dentro il
       container /usr/sbin/policy-rc.d risponde 101, e i maintainer script - che usano
       deb-systemd-invoke e non systemctl proprio per questo - lo rispettano. Vedere
       quella riga nell'uscita di apt e' la conferma che la regola funziona.
#>

[CmdletBinding()]
param(
    [string] $CartellaDiLavoro = (Join-Path $env:TEMP 'observer-prova-esterna'),
    [string] $Nome = 'obs-esterna',
    [switch] $Smonta
)

$ErrorActionPreference = 'Stop'

$radice = Split-Path -Parent $PSScriptRoot

function Passo([string] $testo) {
    Write-Host ''
    Write-Host "==> $testo" -ForegroundColor Cyan
}

if ($Smonta) {
    Passo "Rimuovo il container $Nome"
    podman rm -f $Nome
    Write-Host "Fatto. Token e impronta di quel container non esistono piu': se rifai la" -ForegroundColor Yellow
    Write-Host "prova, machines.json va riscritto con i valori nuovi." -ForegroundColor Yellow
    return
}

Passo 'Controllo che podman sia in piedi'
$macchina = podman machine list --format '{{.Name}} {{.Running}}' 2>$null
if (-not $macchina) { throw 'podman non risponde. Prova con: podman machine start' }
Write-Host $macchina

if (-not (Test-Path $CartellaDiLavoro)) {
    New-Item -ItemType Directory -Path $CartellaDiLavoro | Out-Null
}

$versione = ([xml](Get-Content (Join-Path $radice 'Directory.Build.props'))).Project.PropertyGroup.Version
$pacchetto = "observer_${versione}_amd64.deb"

Passo "Costruisco $pacchetto dentro un container (qualche minuto)"

# Il repository entra in SOLA LETTURA e viene copiato dentro: cosi' la compilazione per
# Linux non riusa gli obj di Windows, e niente di cio' che succede qui puo' sporcare
# l'albero di lavoro.
$ricetta = @'
set -e
apt-get update -qq > /dev/null
apt-get install -y -qq binutils > /dev/null
mkdir -p /work && cd /src
tar -cf - --exclude=./.git --exclude=bin --exclude=obj . | (cd /work && tar -xf -)
cd /work && bash packaging/linux/pack.sh
cp packaging/linux/out/*.deb /out/
'@

podman run --rm `
    -v "${radice}:/src:ro" `
    -v "${CartellaDiLavoro}:/out" `
    mcr.microsoft.com/dotnet/sdk:10.0 `
    bash -c $ricetta

$costruito = Join-Path $CartellaDiLavoro $pacchetto
if (-not (Test-Path $costruito)) { throw "Il pacchetto $pacchetto non e' stato prodotto." }
Write-Host ("Costruito: {0} byte" -f (Get-Item $costruito).Length)

Passo "Preparo la macchina esterna ($Nome)"
podman rm -f $Nome 2>$null | Out-Null
podman run -d --name $Nome --systemd=always --hostname obs-container `
    -p 5058:5058 -v "${CartellaDiLavoro}:/pacchetti:ro" localhost/obs-systemd:latest | Out-Null

Passo 'Installo il pacchetto'
# La riga "policy-rc.d returned 101" qui e' attesa, ed e' una buona notizia.
podman exec $Nome bash -c "apt-get update -qq > /dev/null 2>&1; apt-get install -y -qq /pacchetti/$pacchetto"

Passo 'Avvio il servizio a mano'
podman exec $Nome systemctl start observer.service
$stato = podman exec $Nome systemctl is-active observer.service
if ($stato -ne 'active') { throw "Il servizio non e' partito: $stato" }

# L'ascolto in IPv6 e' la condizione che rende raggiungibile localhost dall'host: se questa
# riga non ci fosse, la dashboard non arriverebbe mai al container, e l'errore che
# comparirebbe non nominerebbe questa causa.
$ascolto = podman exec $Nome journalctl -u observer --no-pager -n 20 |
    Select-String 'Now listening on: https'
Write-Host $ascolto
if ($ascolto -notmatch '\[::\]') {
    Write-Warning 'Il servizio non ascolta in IPv6: da Windows "localhost" non lo raggiungera.'
}

Passo 'Token e impronta'
$condiviso = podman exec $Nome observer share
$token = ($condiviso | Select-String -Pattern '^\s{4}\S{20,}$').Matches.Value.Trim() | Select-Object -First 1
$impronta = ($condiviso | Select-String -Pattern '([0-9A-F]{2}:){31}[0-9A-F]{2}').Matches.Value | Select-Object -First 1

if (-not $token -or -not $impronta) { throw 'Non sono riuscito a leggere token o impronta.' }

Passo 'Provo il trasporto dall''host, prima di aprire la dashboard'
# Senza token la risposta attesa e' 401: e' la prova che TLS regge e che a mancare e' solo
# l'autorizzazione. Un errore di connessione qui, invece, e' un problema di rete.
try {
    Invoke-WebRequest -Uri 'https://localhost:5058/metrics/latest' -SkipCertificateCheck -TimeoutSec 10 | Out-Null
    Write-Warning 'Ha risposto senza token: inatteso.'
}
catch {
    $codice = $_.Exception.Response.StatusCode.value__
    if ($codice -eq 401) { Write-Host 'https://localhost:5058 risponde 401 senza token: il trasporto funziona.' }
    else { throw "Il container non risponde da Windows: $($_.Exception.Message)" }
}

Write-Host ''
Write-Host 'Metti questo in %LOCALAPPDATA%\Observer\machines.json:' -ForegroundColor Green
Write-Host ''

[ordered]@{
    machines = @(
        [ordered]@{
            name        = 'obs-container'
            baseAddress = 'https://localhost:5058/'
            apiToken    = $token
            fingerprint = $impronta
        }
    )
} | ConvertTo-Json -Depth 4

Write-Host ''
Write-Host 'Poi: dotnet run --project src\Observer.App' -ForegroundColor Green
Write-Host 'Attese due macchine nella barra laterale: questa e obs-container.'
Write-Host ''
Write-Host "Per smontare: .\scripts\prova-macchina-esterna.ps1 -Smonta -Nome $Nome"
