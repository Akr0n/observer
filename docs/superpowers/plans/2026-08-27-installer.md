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

### lintian, girato davvero

Le correzioni di packaging erano rimaste **ipotesi**: lintian non gira su Windows, e nessun job
lo eseguiva. Adesso c'e' un passo in `pack-linux` che lo installa e lo esegue sul `.deb`
appena costruito, con `--fail-on error`: stampa tutti i tag e si ferma solo sugli errori.

Delle tre correzioni fatte a memoria, **una sola serviva davvero**. Misurato:

| ipotizzato | verdetto di lintian |
| --- | --- |
| `missing-dependency-on-libc` | **non si presenta.** `libc6` era gia' fra le `Depends` |
| `shared-library-is-executable` | **non si presenta.** Il `chmod 0644` sulle `.so` bastava |
| `unstripped-binary-or-object` | **si presenta**, ed e' un errore: 4 librerie native |

E ne sono usciti **tre errori che nessuno aveva previsto** — `embedded-library` per `freetype`,
`libjpeg` e `libpng`, tutti e tre dentro `libSkiaSharp.so` — piu' una fila di avvertimenti veri.

Cosa e' stato corretto, e non zittito:

- **`unstripped-binary-or-object`**: `strip --strip-unneeded` sulle `.so` native. Verificato
  che non le rompa: dopo lo strip il servizio parte e risponde `200` su `/metrics/latest`
  scrivendo il database, cioe' `libe_sqlite3.so` funziona ancora.
- **`non-standard-executable-perm` e `executable-not-elf-or-script`**: i permessi che escono da
  `dotnet publish` non sono quelli di un pacchetto Debian. Le `.dll` gestite arrivano `0744`,
  e **`appsettings.json` arriva `0777`**. Quest'ultimo non e' cosmetico: un file di
  configurazione scrivibile da chiunque, che il servizio rilegge a ogni avvio, oggi e' tappato
  soltanto dal permesso della cartella che lo contiene. Ora e' tutto `0644` tranne i tre
  eseguibili veri.
- **`wrong-name-for-changelog-of-native-package`**: la versione non ha revisione Debian, quindi
  il pacchetto e' *nativo*, e per un nativo il changelog si chiama `changelog.gz` e non
  `changelog.Debian.gz`.
- **`maintainer-script-has-unexpanded-debhelper-token`**: nel `postinst` c'era un `#DEBHELPER#`
  che nessuno espandeva — il pacchetto non usa debhelper. Testo morto, spedito a tutti.
- **`no-manual-page`**: due comandi in `/usr/bin` senza pagina di manuale. Scritte.

L'unico tag **sovrascritto** e' `embedded-library`, e per una ragione che non si puo' aggirare:
il `libSkiaSharp.so` dei pacchetti NuGet porta `freetype`, `libjpeg` e `libpng` compilati
dentro, e una variante collegata alle librerie di sistema non esiste. Il costo va scritto
invece che nascosto: **una vulnerabilita' in una di quelle tre non si chiude aggiornando
Debian.** Si chiude aggiornando SkiaSharp e ricostruendo il pacchetto.

### `maintainer-script-calls-systemctl`: non era un avvertimento cosmetico

Era stato lasciato a vista dicendo che cambiarlo alla cieca sarebbe stato uno scambio peggiore.
Poi si e' scoperto che **alla cieca non era necessario**: podman esegue systemd davvero
(`podman run --systemd=always` su un'immagine `ubuntu:24.04` con `systemd systemd-sysv dbus`,
e `systemctl is-system-running` risponde `degraded`, cioe' avviato). Quindi il confronto si e'
potuto fare **misurando**, costruendo due `.deb` identici in tutto tranne gli script di
manutenzione.

L'immagine ufficiale di Ubuntu porta gia' `/usr/sbin/policy-rc.d` con dentro `exit 101`, che e'
proprio lo scenario reale: *installa pure, ma non avviare niente.*

| | `UnitFileState` | avviato? |
| --- | --- | --- |
| **vecchio** (`systemctl start`), `policy-rc.d` = 101 | `enabled` | **si, `2026-08-27 16:53:31`** |
| **nuovo** (`deb-systemd-invoke`), `policy-rc.d` = 101 | `enabled` | **no, campo vuoto** |
| nuovo, senza `policy-rc.d` | `enabled` | si |

La riga che conta e' la prima: il `postinst` di prima **avviava il servizio dove
l'amministratore aveva scritto di non farlo**. Non era una pedanteria di lintian, era il
comportamento sbagliato, e la guardia `[ -d /run/systemd/system ]` non lo copriva — quella
guardia risponde a "systemd sta girando?", che e' un'altra domanda.

`deb-systemd-helper` porta in dote la seconda meta': tiene lo **stato** dell'abilitazione.
Misurato sull'intero ciclo — installa, `dpkg -r`, `dpkg -P`, reinstalla:

- dopo `dpkg -r` l'unit viene mascherata (`observer.service -> /dev/null`), perche' il file
  dell'unit non c'e' piu' ma il collegamento in `multi-user.target.wants` resta;
- dopo `dpkg -P` la maschera **e** lo stato salvato spariscono, e `/etc/observer` sopravvive,
  che e' esattamente cio' che il `postrm` promette;
- reinstallando, il servizio torna `enabled` e **riparte**.

Con `systemctl disable` niente di tutto questo esisterebbe: non tiene alcuno stato, quindi
dopo un purge lascerebbe dietro di se' cio' che aveva acceso.

Correggendolo sono comparsi due tag nuovi, `command-with-path-in-maintainer-script`: il
percorso `/usr/bin/deb-systemd-helper` scritto a mano dentro un `[ -x ... ]`. Il pattern
generato da debhelper lo fa, ma lintian riconosce i propri script e li esenta; i nostri sono
scritti a mano, quindi il tag scatta. Sostituito con `command -v`, che e' anche piu' corretto.

### Una cosa data per vera e mai verificata, ora verificata

Il piano affermava che *"Ubuntu 24.04 ha .NET 10 nel proprio archivio ufficiale, in `main`"*, ed
e' l'affermazione su cui poggia la scelta di **non** imbarcare il runtime nel pacchetto. Non era
mai stata controllata. `apt-cache policy` dentro il contenitore risponde
`Candidate: 10.0.11-0ubuntu1~24.04.1` e l'installazione simulata riesce: **e' vera**, e la
dipendenza `aspnetcore-runtime-10.0` e' soddisfacibile.

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

## La barra rossa del primo secondo — chiusa

Era l'ultima riga di "cosa resta aperto", e valeva la pena chiuderla prima di distribuire: la
dashboard si apriva **rossa** su ogni macchina appena installata, perche' il primo tentativo
cadeva mentre il servizio stava ancora partendo, e l'errore spariva da solo un attimo dopo. Un
allarme che si spegne da solo insegna a ignorare anche quelli veri.

La causa non era il messaggio ma **da cosa dipendeva la gravita'**: dal singolo tentativo
andato male, invece che da quanto dura il guasto. Un servizio irraggiungibile da un secondo e'
un servizio che sta partendo; da mezzo minuto e' un servizio che non c'e'. La decisione sta ora
in `StatusEscalation`, funzione pura con la sua tabella provata, e il view model si limita a
misurare il tempo e a tradurre in colore.

Due cose che questa modifica **non** e':

- **Non e' sopprimere l'allarme.** Scaduta la tolleranza la barra diventa rossa lo stesso, con
  dentro il dettaglio tecnico che durante l'attesa si tace perche' e' rumore. C'e' un test per
  ognuno dei due versi: se l'escalation smettesse di scattare, il secondo fallirebbe.
- **Non vale per tutto.** L'attesa serve solo dove aspettare puo' cambiare l'esito. Un token
  rifiutato, una versione incompatibile o una risposta illeggibile saranno identici fra un
  minuto: restano rossi dal primo tentativo.

La tolleranza e' **dieci secondi**, e il numero e' misurato, non scelto: dall'avvio del
processo alla prima risposta `200` su `/metrics/latest` passano **0,9-1,4 secondi** su tre
giri, con il servizio avviato a mano e i binari gia' scaldati. Su una macchina appena
installata il costo e' piu' alto — cache dei file fredda, antivirus che scandisce i binari
appena scritti, avvio mediato dal gestore dei servizi — e dieci secondi lasciano un margine
largo senza far sembrare bloccata la finestra di chi apre la dashboard dove il servizio non c'e'.

Nello stesso passaggio e' emerso il **gemello silenzioso**, altrettanto sbagliato e mai notato:
un servizio vivo che risponde `503` senza mai campionare restava *"Service is starting"* **per
sempre**, con un testo che promette che si risolve da solo in un secondo o due. Se non si
risolve, quella frase e' una bugia che nessuno smentisce mai. Adesso, scaduta la stessa
tolleranza, diventa un avvertimento che manda a `observer doctor`.
