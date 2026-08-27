<#
.SYNOPSIS
    Installa, disinstalla o interroga Observer.Service come servizio di Windows.

.DESCRIPTION
    Richiede una PowerShell ELEVATA: registrare un servizio modifica impostazioni di sistema.
    Lo script si rifiuta di procedere senza elevazione invece di fallire a meta'.

    Non genera, non chiede e non conosce alcun token: dal piano 3 il servizio se lo genera e
    se lo custodisce da solo al primo avvio. Se nella cartella pubblicata c'e' comunque un
    appsettings.Local.json - un token scelto a mano, che vince su quello generato - lo copia e
    ne RESTRINGE i permessi, perche' la cartella di destinazione e' leggibile da chiunque abbia
    un account e un token di macchina non deve esserlo.

.PARAMETER Azione
    Installa, Disinstalla, Stato oppure Verifica.

    Verifica NON richiede elevazione, ed e' deliberato: la domanda a cui risponde e' se
    l'utente interattivo riesca ad aprire la pipe di un servizio che gira come LocalSystem.
    Posta da una console elevata, quella domanda avrebbe sempre risposta "si" e non
    proverebbe niente.

.PARAMETER Sorgente
    La cartella prodotta da "dotnet publish -c Release -o artifacts/service".

.PARAMETER Destinazione
    Dove installare. Predefinito: C:\Program Files\Observer.

.EXAMPLE
    .\scripts\servizio-windows.ps1 -Azione Installa -Sorgente .\artifacts\service

.EXAMPLE
    .\scripts\servizio-windows.ps1 -Azione Disinstalla
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Installa', 'Disinstalla', 'Stato', 'Verifica')]
    [string] $Azione,

    [string] $Sorgente = '.\artifacts\service',

    [string] $Destinazione = 'C:\Program Files\Observer'
)

$ErrorActionPreference = 'Stop'
$nomeServizio = 'Observer'

function Test-Elevazione {
    $identita = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principale = New-Object Security.Principal.WindowsPrincipal($identita)
    return $principale.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Show-Stato {
    $servizio = Get-Service -Name $nomeServizio -ErrorAction SilentlyContinue

    if (-not $servizio) {
        Write-Host "Il servizio '$nomeServizio' non e' registrato."
        return
    }

    $wmi = Get-CimInstance -ClassName Win32_Service -Filter "Name='$nomeServizio'"
    Write-Host "Nome        : $($servizio.Name)"
    Write-Host "Stato       : $($servizio.Status)"
    Write-Host "Avvio       : $($servizio.StartType)"
    Write-Host "Account     : $($wmi.StartName)"
    Write-Host "Eseguibile  : $($wmi.PathName)"
}

if ($Azione -eq 'Stato') {
    Show-Stato
    return
}

if ($Azione -eq 'Verifica') {
    Show-Stato
    Write-Host ''

    $chiSono = [Security.Principal.WindowsIdentity]::GetCurrent()
    Write-Host "Verifica eseguita come: $($chiSono.Name)"
    Write-Host "Elevata: $(Test-Elevazione)  (una verifica elevata non proverebbe nulla)"
    Write-Host ''

    $tubo = New-Object System.IO.Pipes.NamedPipeClientStream(
        '.', $nomeServizio, [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::None,
        [System.Security.Principal.TokenImpersonationLevel]::Identification)

    try {
        # "." e non "localhost": localhost passerebbe da SMB e il servizio classificherebbe la
        # connessione come proveniente dalla rete.
        $tubo.Connect(3000)
        Write-Host 'ESITO: la pipe si apre. La DACL lascia entrare questo utente.' -ForegroundColor Green
        Write-Host 'Il token resta comunque obbligatorio: aprire la pipe non e'' essere autorizzati.'
    }
    catch [System.UnauthorizedAccessException] {
        Write-Host 'ESITO: ACCESSO NEGATO alla pipe.' -ForegroundColor Red
        Write-Host 'La DACL chiude fuori questo utente: la GUI non potrebbe collegarsi.'
    }
    catch [System.TimeoutException] {
        Write-Host 'ESITO: la pipe non esiste (timeout).' -ForegroundColor Yellow
        Write-Host 'Il servizio non e'' in esecuzione, oppure il canale locale e'' disabilitato.'
    }
    finally {
        $tubo.Dispose()
    }

    return
}

if (-not (Test-Elevazione)) {
    Write-Error @'
Questa azione richiede una PowerShell eseguita come amministratore.
Aprine una elevata e rilancia lo stesso comando.
'@
    return
}

if ($Azione -eq 'Disinstalla') {
    $servizio = Get-Service -Name $nomeServizio -ErrorAction SilentlyContinue

    if ($servizio) {
        if ($servizio.Status -ne 'Stopped') {
            Stop-Service -Name $nomeServizio -Force
            $servizio.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        }

        Remove-Service -Name $nomeServizio
        Write-Host "Servizio '$nomeServizio' rimosso."
    }
    else {
        Write-Host "Il servizio '$nomeServizio' non era registrato."
    }

    # I file NON vengono cancellati: contengono il database dello storico, e cancellarli
    # sarebbe una sorpresa. Rimuovili a mano se e quando vuoi.
    Write-Host "I file in '$Destinazione' sono stati lasciati dove sono."
    return
}

# --- Installa ---

$Sorgente = (Resolve-Path -Path $Sorgente).Path
$eseguibileSorgente = Join-Path $Sorgente 'Observer.Service.exe'

if (-not (Test-Path $eseguibileSorgente)) {
    Write-Error "Non trovo '$eseguibileSorgente'. Esegui prima: dotnet publish src/Observer.Service -c Release -o artifacts/service"
    return
}

# appsettings.Local.json NON e' piu' obbligatorio. Dal piano 3 il servizio si genera e si
# custodisce il proprio token di macchina al primo avvio, sotto C:\ProgramData\Observer: e'
# esattamente cio' che permette a questo script di non conoscere alcun segreto.
# Se il file c'e' comunque, viene copiato e i suoi permessi vengono ristretti, perche' un token
# scelto a mano vince sul deposito e va protetto come quello generato.
$configurazioneLocale = Join-Path $Sorgente 'appsettings.Local.json'
# Un file VUOTO conta come assente, esattamente come lo tratta il servizio: svuotarlo e'
# il gesto naturale per togliere il token, e annunciare 'trovato un token' su zero byte
# manderebbe fuori strada chi legge.
$portaUnToken = (Test-Path $configurazioneLocale) -and
    -not [string]::IsNullOrWhiteSpace((Get-Content $configurazioneLocale -Raw -ErrorAction SilentlyContinue))

if ($portaUnToken) {
    Write-Host 'Trovato appsettings.Local.json: il token che contiene vincera'' su quello generato.'
}
else {
    Write-Host 'Nessun token scelto a mano: il servizio generera'' da se'' il proprio, sotto ProgramData.'
}

$esistente = Get-Service -Name $nomeServizio -ErrorAction SilentlyContinue

if ($esistente) {
    Write-Host "Il servizio esiste gia': lo fermo e lo rimuovo prima di reinstallarlo."

    if ($esistente.Status -ne 'Stopped') {
        Stop-Service -Name $nomeServizio -Force
        $esistente.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }

    Remove-Service -Name $nomeServizio
    Start-Sleep -Seconds 2
}

if (-not (Test-Path $Destinazione)) {
    New-Item -ItemType Directory -Path $Destinazione | Out-Null
}

Copy-Item -Path (Join-Path $Sorgente '*') -Destination $Destinazione -Recurse -Force
Write-Host "File copiati in '$Destinazione'."

# Se un token scelto a mano c'e', va protetto: la cartella di installazione e' leggibile da
# chiunque abbia un account sulla macchina. Si toglie l'ereditarieta' e si concede solo a
# SYSTEM, che esegue il servizio, e agli amministratori, che devono poterlo cambiare.
# Il token GENERATO dal servizio non passa di qui: vive gia' protetto sotto ProgramData.
if ($portaUnToken) {
    $configurazioneInstallata = Join-Path $Destinazione 'appsettings.Local.json'
    & icacls.exe $configurazioneInstallata /inheritance:r /grant 'NT AUTHORITY\SYSTEM:(R)' /grant 'BUILTIN\Administrators:(F)' | Out-Null
    Write-Host "Permessi ristretti su '$configurazioneInstallata' (solo SYSTEM e amministratori)."
}

$eseguibileInstallato = Join-Path $Destinazione 'Observer.Service.exe'

# Nessun -Credential: senza, New-Service registra il servizio come LocalSystem, che e'
# esattamente l'account voluto. Le virgolette dentro la stringa servono perche' il percorso
# contiene spazi.
New-Service -Name $nomeServizio `
            -BinaryPathName ('"' + $eseguibileInstallato + '"') `
            -DisplayName 'Observer metrics service' `
            -Description 'Samples CPU and memory and serves them over HTTP and a local named pipe.' `
            -StartupType Automatic | Out-Null

Start-Service -Name $nomeServizio
Start-Sleep -Seconds 3

Show-Stato