# Installer — piano di implementazione

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans.

**Goal:** un pacchetto che installi servizio e dashboard, e che **non conosca alcun segreto**.

Dal piano 3 il servizio genera e custodisce da solo il proprio token: l'installer copia file,
registra un servizio, crea un collegamento. Nient'altro.

## Le tre cose che possono fare danno

In cima, perche' sono quelle che si scoprono tardi.

### 1. Il progetto WiX NON va messo in `Observer.slnx`

`WixToolset.Sdk` porta binari nativi **solo per Windows** (`runtimes/win-x64/native/wixnative.exe`,
`win-arm64`, `win-x86`, `x64/burn.exe`; nessun `runtimes/linux-*`), e il target
`WindowsInstallerValidation` **non ha alcuna condizione sul sistema operativo**: gira sempre e
usa il motore ICE di Windows Installer.

La catena che ne segue e' quella gia' documentata come l'unico modo di bloccarsi fuori dal
repository: `CLAUDE.md` impone di registrare ogni nuovo progetto nella soluzione, la CI
costruisce la soluzione **anche su `ubuntu-latest`**, e i due contesti `build (ubuntu-latest)` e
`build (windows-latest)` sono check obbligatori del ruleset con `bypass_actors` vuoto. Un
`.wixproj` dentro la soluzione renderebbe **ogni PR rossa per sempre**.

Quindi: il `.wixproj` sta **fuori** dalla soluzione, e si costruisce in un **job CI aggiuntivo**
condizionato a Windows. Aggiungere un job nuovo e' sicuro; rinominare `build` o i valori della
matrice `os` no. **Ne' si aggiunge una seconda dimensione alla matrice**: quella cambierebbe i
nomi dei contesti richiesti, ed e' la mossa che blocca il repository — non la rinomina.

### 2. Un harvest a stella impacchetta il token

Misurato: con `<Files Include="publish\**" ... />` la tabella `File` dell'MSI contiene anche
`appsettings.Local.json`. Cioe' il file che lo script di installazione tratta esplicitamente
come *possibile token scelto a mano* finirebbe **dentro un MSI destinato a GitHub Releases**.

Il vincolo del piano 6 non e' "l'installer non genera un token": e' **"l'installer non lo
conosce"**. Un harvest a stella lo conosce e lo redistribuisce.

Servono due cose, non una: l'esclusione esplicita nel `.wxs`, **e** una guardia che faccia
fallire la build se quel file e' presente nel payload. L'esclusione da sola si dimentica il
giorno che qualcuno rinomina il file.

### 3. La validazione ICE gira gia', e non e' `wix build`

`wix build` da riga di comando **non** valida: produce l'MSI e tace. Sul percorso MSBuild
(`.wixproj` piu' `dotnet build`) il target `WindowsInstallerValidation` gira invece **sempre**, e
un `.wxs` sbagliato fa fallire la build con `ICE38`, `ICE43`, `ICE57`, `ICE90`.

E' lo stesso schema gia' noto altrove nel progetto — *il comando che sembra il controllo non e'
il controllo* — con il verso invertito: qui la validazione e' automatica, incondizionata e
legata a Windows, ed e' proprio cio' che rende impossibile costruire il pacchetto su
`ubuntu-latest`.

## La scelta dello strumento

**WiX 6.0.2**, come `.wixproj` con `WixToolset.Sdk`.

| versione | costruisce | note |
| --- | --- | --- |
| 5.0.2 | si | licenza `MS-RL`, nessuna accettazione |
| **6.0.2** | **si** | licenza `MS-RL`, nessuna accettazione |
| 7.0.0 | **no** | `error WIX7015`: serve accettare la EULA della Open Source Maintenance Fee |

WiX 7 si sblocca con `wix eula accept wix7`, un comando solo. **Non e' stato dato**: accettare un
accordo legale e' una decisione di chi possiede il progetto, non di chi scrive il codice. Per la
cronaca, il testo della EULA e' stato letto: la tariffa si applica solo a chi usa il software in
attivita' che generano ricavi **e** con ricavi lordi annui da 10.000 dollari in su, con esenzione
esplicita sotto quella soglia; non e' una licenza d'uso, e il codice resta sotto licenza OSI. Su
un progetto personale non comporta alcun pagamento. Resta una decisione da prendere, e finche'
non la si prende si usa la 6.

Scartate: **Inno Setup** non sa registrare un servizio e lo delega a `sc.exe`, cioe' rinuncia al
rollback transazionale; **MSIX** e il packaging di `dotnet publish` non arrivano a registrare un
servizio di sistema.

## Cosa mette il pacchetto

Un solo MSI **per-macchina** (`Scope="perMachine"`), che copre entrambi i casi: il servizio deve
essere per-macchina, e con `ProgramMenuFolder` il collegamento alla dashboard finisce nel menu
Start di **tutti** gli utenti. Non servono due pacchetti.

### Il servizio

```xml
<Component Id="ServiceExecutable" Directory="INSTALLFOLDER" Guid="...">
  <File Id="ObserverServiceExe" Source="payload\Observer.Service.exe" KeyPath="yes" />
  <ServiceInstall Id="ObserverServiceInstall" Name="Observer"
                  DisplayName="Observer metrics service"
                  Description="Samples CPU and memory and serves them over HTTP and a local named pipe."
                  Type="ownProcess" Start="auto" Account="LocalSystem"
                  ErrorControl="normal" Vital="yes" />
  <ServiceControl Id="ObserverServiceControl" Name="Observer"
                  Start="install" Stop="both" Remove="uninstall" Wait="yes" />
</Component>
```

`Account="LocalSystem"` e' **esplicito**: ometterlo non equivale al `New-Service` senza
`-Credential`. `Stop="both"` ferma il servizio sia in installazione sia in disinstallazione, ed
e' cio' che rende sicuro l'aggiornamento su un servizio in esecuzione.

Tre righe di XML sostituiscono `New-Service` piu' `Start-Service` piu' `Stop-Service` piu'
`Remove-Service` dello script attuale, e in piu' portano il **rollback transazionale**: se
l'installazione fallisce a meta', Windows Installer rimette indietro anche il servizio.

### Il collegamento

La prima stesura era sbagliata e la validazione l'ha presa: uno `Shortcut` in un componente a se'
con `KeyPath` in `HKLM` produce `ICE38`, `ICE43` e `ICE57`. La forma che valida a zero errori ha
lo `Shortcut` **annidato dentro l'elemento `File`** dell'eseguibile, con `Advertise="yes"`, e un
`Id` di directory in maiuscolo/minuscolo misto — `ObserverMenuFolder` e non
`OBSERVERMENUFOLDER`, che MSI interpreterebbe come proprieta' pubblica (`ICE90`).

## Cosa NON fa il pacchetto

- **Non genera, non chiede e non registra alcun token.** Il servizio se lo procura al primo
  avvio.
- **Non tocca `C:\ProgramData\Observer`.** Quella cartella la crea e la ripara il servizio a ogni
  avvio, e ha una DACL protetta che l'installer non deve toccare.
- **Non cancella dati in disinstallazione.** L'MSI rimuove cio' che ha installato lui; il
  deposito delle credenziali e lo storico restano.

## Un difetto misurato che l'installer NON risolve

Oggi il database dello storico finisce nel profilo di LocalSystem:
`C:\WINDOWS\system32\config\systemprofile\AppData\Local\Observer\observer.db` — misurato
interrogando il servizio VIVO dal canale locale, 7,4 MB.

L'idea ovvia — spostarlo in `C:\ProgramData\Observer`, accanto alle credenziali — **non
funziona**, ed e' stato misurato: quella cartella e' illeggibile a un utente non elevato al punto
che perfino `icacls` risponde *Accesso negato*, perche' la DACL protetta del deposito si applica
anche alle sottocartelle. Si scambierebbe una cartella illeggibile con un'altra, portando per
giunta un database scritto di continuo (con `-wal` e `-shm`) **dentro il perimetro del segreto**,
che il servizio ripara a ogni avvio.

Se lo storico deve essere ispezionabile serve una cartella **fuori** dal deposito, con una ACL
propria. E' una decisione di progetto a se', e non appartiene all'installer.

## Il pacchetto Linux

Misurato su Ubuntu 24.04 vera, con i binari veri: il `.deb` pesa **839 KB**.

- **`dpkg-deb`**, gia' presente su `ubuntu-latest`. Nessuno strumento .NET: uno e' fermo,
  l'altro e' a pagamento.
- **Non self-contained.** Ubuntu 24.04 ha .NET 10 nel proprio archivio ufficiale, in `main`.
- **Il servizio NON gira come root.** Un utente di sistema dedicato funziona, purche' sia il
  `postinst` a creare `/etc/observer`. Misurato girando come utente `observer`: scrive
  `/etc/observer/credentials.json` come `-rw------- observer observer`, il socket nasce
  `srw-rw---- observer observer`, e un utente **nel** gruppo `observer` ottiene `200` senza
  token mentre uno **fuori** si vede rifiutare la connessione. E' esattamente il modello del
  piano 1, ottenuto dai permessi del file invece che da una DACL.
- **`Type=notify`**, piu' `RuntimeDirectory=` e `StateDirectory=`.
- **`WorkingDirectory=` e' obbligatorio.** Senza, il servizio non legge `appsettings.json`, perde
  in silenzio la sezione `Kestrel` e ripiega su `http://localhost:5000`: non ascolta piu' sulla
  rete e nessuno se ne accorge.
- **`Observer:Storage:DatabasePath` va reso assoluto** nell'unit, altrimenti il database finisce
  nella home di root e `ProtectHome=yes` lo nasconde.
- Le dipendenze di sistema della dashboard vanno **misurate**, non ricordate: l'elenco ovvio
  (`libx11-6`, `libice6`, `libsm6`, `libfontconfig1`) e' incompleto. Sotto Xvfb la mappa del
  processo mostra anche `libXext`, `libXrandr`, `libXrender`, `libX11-xcb`, `libGL`, `libGLX`.

## Task

1. `.wixproj` piu' `.wxs` **fuori** dalla soluzione, con la guardia sul payload.
2. Un job CI `pack-windows` condizionato a Windows, senza toccare la matrice esistente.
3. Il `.deb` e l'unit systemd, con un job `pack-linux`.
4. L'icona: **il progetto non ne ha una**, e senza, il collegamento nel menu Start mostra l'icona
   generica di `apphost`. E' una lacuna da colmare prima di distribuire.

## Cosa resta aperto

- **La firma.** L'MSI non firmato accende SmartScreen quando arriva da Internet. Il certificato
  EV non salta piu' SmartScreen, e Azure Artifact Signing non e' aperto agli individui fuori da
  Stati Uniti e Canada. Nessuna strada gratuita: e' un costo ricorrente, oppure si accetta
  l'avviso.
- **Il servizio gia' registrato a mano.** Dove esiste un servizio `Observer` creato da
  `New-Service`, `MajorUpgrade` non lo conosce e non lo gestisce. Va disinstallato con
  `scripts/servizio-windows.ps1 -Azione Disinstalla` **prima** del primo MSI.
- **La dashboard mostra una barra rossa** a chi la apre mentre il servizio si sta avviando.
  Misurato. Ed e' il primo secondo di vita del programma su una macchina appena installata.
