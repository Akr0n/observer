<#
.SYNOPSIS
    Costruisce l'MSI di Observer.

.DESCRIPTION
    Pubblica i tre eseguibili in una cartella di payload e costruisce il pacchetto.
    Non richiede elevazione: costruire un MSI e' un'operazione ordinaria, installarlo no.

    Il progetto WiX sta DELIBERATAMENTE fuori da Observer.slnx: WixToolset.Sdk porta binari
    nativi solo per Windows e la validazione ICE gira sempre, quindi dentro la soluzione
    farebbe fallire per sempre il job "build (ubuntu-latest)" della CI, che e' un check
    obbligatorio del ruleset.

.PARAMETER Configurazione
    Release oppure Debug.

.EXAMPLE
    .\packaging\windows\pack.ps1
#>
[CmdletBinding()]
param(
    [string] $Configurazione = 'Release'
)

$ErrorActionPreference = 'Stop'

$radice = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$payload = Join-Path $PSScriptRoot 'payload'

if (Test-Path $payload) {
    Remove-Item $payload -Recurse -Force
}

foreach ($progetto in 'Observer.Service', 'Observer.App', 'Observer.Cli') {
    Write-Host "Pubblico $progetto..."

    # Mirato a win-x64: senza, SkiaSharp spedisce le librerie native di OGNI piattaforma e il
    # pacchetto passa da una decina di megabyte a oltre cento.
    & dotnet publish (Join-Path $radice "src\$progetto") `
        -c $Configurazione -r win-x64 --self-contained false `
        -o $payload --nologo | Out-Null

    if ($LASTEXITCODE -ne 0) {
        throw "La pubblicazione di $progetto e' fallita."
    }
}

# appsettings.Local.json e' il file dove uno sviluppatore tiene il proprio token, e "dotnet
# publish" lo porta con se'. Va tolto dal payload PRIMA di impacchettare: un MSI finisce su
# GitHub Releases. Il progetto WiX ha comunque una guardia che fa fallire la build se lo trova,
# ma trovarselo qui e' il caso normale, non un'anomalia da segnalare.
Get-ChildItem $payload -Filter 'appsettings*.Local.json' -ErrorAction SilentlyContinue |
    ForEach-Object {
        Write-Host "Tolgo dal payload: $($_.Name)"
        Remove-Item $_.FullName -Force
    }

Write-Host 'Costruisco il pacchetto...'
& dotnet build (Join-Path $PSScriptRoot 'Observer.wixproj') -c $Configurazione --nologo

if ($LASTEXITCODE -ne 0) {
    throw 'La costruzione del pacchetto e'' fallita.'
}

$msi = Get-ChildItem (Join-Path $PSScriptRoot 'bin') -Recurse -Filter 'Observer.msi' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

Write-Host ''
Write-Host ("Pacchetto: {0} ({1:N1} MB)" -f $msi.FullName, ($msi.Length / 1MB))
Write-Host ''
Write-Host 'Per installarlo serve un terminale ELEVATO:'
Write-Host ("    msiexec /i `"{0}`"" -f $msi.FullName)
Write-Host ''
Write-Host 'Se su questa macchina esiste gia'' un servizio Observer registrato a mano con'
Write-Host 'scripts\servizio-windows.ps1, disinstallalo PRIMA: il pacchetto non lo conosce e'
Write-Host 'non lo gestisce.'
Write-Host ''
Write-Host 'E controlla che la cartella di installazione sia VUOTA. I binari copiati a mano'
Write-Host 'vengono da un publish senza identificatore di piattaforma, e portano una'
Write-Host 'System.ServiceProcess.ServiceController.dll che su Windows non funziona: a parita'''
Write-Host 'di versione Windows Installer NON la sostituisce, il servizio non parte, e'
Write-Host "l'installazione si ferma con un errore 1920 che parla di privilegi insufficienti"
Write-Host 'e non nomina la vera causa.'