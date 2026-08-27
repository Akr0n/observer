# Token, canale locale e installer — specifica e suddivisione

> **Stato:** deciso il 2026-08-27, corretto il 2026-08-27 dopo una campagna di misure.
> Questo documento NON e' un piano eseguibile: e' la specifica e la suddivisione in sei piani.
> Ogni piano va scritto quando lo si affronta, con la skill `superpowers:writing-plans`.

## La decisione

Sulla macchina locale **il sistema operativo sa gia' chi sta chiamando**, quindi un segreto
condiviso e' lo strumento sbagliato. Il bearer token resta, ma solo per le connessioni remote.

- **Windows**: named pipe `\\.\pipe\Observer`, chiamante identificato dal SID.
- **Linux**: socket unix `/run/observer/observer.sock`, chiamante identificato da `SO_PEERCRED`.
- **Rete**: `0.0.0.0:5057` con bearer token, come oggi, **piu' HTTPS** (vedi piano 5).

### La regola che traduce la decisione in codice

**Il trasporto instrada, l'identita' concede, l'identita' non determinabile rifiuta.**

Va scritta qui perche' la prima stesura di questo documento la dava per implicita, e quattro
implementazioni indipendenti hanno tutte scritto la stessa riga sbagliata:

```csharp
if (context.Features.Get<IConnectionNamedPipeFeature>() is not null) { /* salta il token */ }
```

Quella riga apre la telemetria alla rete credendo di chiuderla. Il motivo e' nella sezione
seguente. L'accesso non e' concesso perche' la richiesta e' arrivata dalla pipe, ma perche' il
chiamante e' un principal ammesso su questa macchina.

### Una named pipe NON e' un canale locale

E' un fatto di Windows, non una scelta di Observer: le named pipe sono raggiungibili **da remoto
attraverso SMB, sulla porta 445**, tramite la condivisione `IPC$`. La porta e' aperta verso la
rete locale per ragioni che non hanno niente a che vedere con questo programma.

Misurato: aprendo `NamedPipeClientStream` con `serverName` uguale all'indirizzo di rete della
macchina invece di `"."`, la connessione arriva, e `Get-NetTCPConnection -LocalPort 445` mostra
la sessione TCP corrispondente solo mentre la connessione e' aperta.

### Come si risponde davvero a "sono locale?"

Non dal trasporto, e **nemmeno dal token del chiamante**. Verificato: sul percorso SMB verso la
macchina stessa, Windows restituisce al server il *token interattivo originale*, identico nei 13
SID di gruppo a quello della via locale. Il SID `NETWORK` (S-1-5-2) e' **assente in entrambi i
casi**, quindi una regola "rifiuta se NETWORK" non discrimina nulla in quello scenario.

La risposta viene da una singola chiamata Win32 sull'handle della pipe,
**`GetNamedPipeClientComputerName`**:

| `serverName` usato dal client | esito della chiamata | conclusione |
| --- | --- | --- |
| `"."` | fallisce, `ERROR_PIPE_LOCAL` (229) | chiamante **locale** |
| indirizzo di rete della macchina | riesce, restituisce il nome | arrivato **via SMB** |
| `"localhost"` | riesce, restituisce `[::1]` | arrivato **via SMB** |

Tre cose da non perdere:

1. **`localhost` NON e' la via locale.** Solo `"."` lo e'. Un client scritto con `localhost`
   verrebbe classificato remoto e rifiutato. Vale per il piano 4.
2. Il discriminante **funziona anche quando l'identita' non e' leggibile** — cioe' proprio nel
   caso di attacco descritto al punto seguente.
3. Il chiamante che passa da SMB verso questa stessa macchina e' comunque un utente
   interattivo di questa macchina, quindi non e' un'escalation; ma la difesa non lo ferma per
   merito proprio, e va detto.

### L'identita' la OFFRE il client, non la impone il server

`TokenImpersonationLevel` lo sceglie chi chiama. Con `Anonymous` la richiesta arriva lo stesso,
ma `WindowsIdentity.GetCurrent(ifImpersonating: true)` lancia `SecurityException` con
`HRESULT 0x80070543` (`ERROR_BAD_IMPERSONATION_LEVEL`): il chiamante si e' reso
**unilateralmente non identificabile**.

Quindi il fallimento dell'identificazione non e' una condizione da registrare nel log: e' il
caso di attacco, e deve produrre un rifiuto. Misurato: una guardia che cattura solo
`IOException` e `UnauthorizedAccessException` lascia sfuggire la `SecurityException`, e il
servizio risponde **500** invece di 401 proprio sul percorso che si stava cercando di chiudere.

Nota: `TokenImpersonationLevel.None` non e' un valore neutro. Non specificare il livello fa
applicare a Windows il default della pipe, che e' `Impersonation`: una guardia che rifiutasse
`None` non rifiuterebbe nulla.

### Perche' questa e non le alternative

Due delle tre proposte valutate SPOSTAVANO il segreto in un posto piu' comodo (un deposito
consegnato a richiesta, un file leggibile da un gruppo). Questa lo ELIMINA dal caso comune.

La differenza pratica: con le altre, ogni utente della macchina ha in mano una credenziale
valida **anche dalla rete**, portatile, permanente e non revocabile singolarmente. Con questa
non ha niente da portare via, e `Token rejected` in locale diventa un errore che non puo' piu'
accadere perche' non c'e' piu' nessun token da sbagliare.

Il canale locale resta **HTTP/1.1**: `MetricsClient`, `ServiceOutcome`, il 503 che diventa
"Service is starting" e il controllo su `MachineSnapshot.CurrentSchemaVersion` continuano a
valere identici sui due canali. Non nasce un secondo protocollo da mantenere. Due eccezioni
misurate, da non dimenticare nel piano 4:

- il timeout a 3 secondi **non** si comporta allo stesso modo. Su TCP un servizio spento
  fallisce in millisecondi; su una pipe assente la connect consuma l'intero timeout. Serve un
  connect-timeout esplicito e corto dentro il `ConnectCallback`, distinto da quello della
  richiesta, altrimenti la finestra passa da un aggiornamento al secondo a uno ogni quattro;
- `ServiceOutcome.TokenRifiutato` e il suo testo parlano di `Observer:ApiToken`. Sul canale
  locale non esiste alcun token da correggere: serve un esito nuovo, altrimenti il messaggio
  manda l'utente a cercare un file che non c'e'.

## Le decisioni prese da Federico

| Domanda | Scelta |
| --- | --- |
| Come si autentica il client locale | **Identita' del sistema operativo**, non un token |
| HTTPS sul percorso remoto | **Adesso**, insieme al resto |
| Portata | Progetto completo, non la versione minima |

**Chi legge le metriche in locale senza token** non e' piu' rimandabile "al piano
corrispondente": e' letteralmente il contenuto della `PipeSecurity` e del modo del socket,
cioe' l'output del piano 1. Proposta, da confermare quando si scrive quel piano: **gli utenti
con una sessione interattiva sulla macchina**. Su Windows una ACE per
`NT AUTHORITY\INTERACTIVE` (S-1-5-4), **non** per `Authenticated Users`; su Linux directory
`0750` e socket `0660`, con autorizzazione sull'uid letto da `SO_PEERCRED`.

Resta invece davvero da decidere: **regola firewall su Windows**, attiva subito oppure solo
dopo un gesto esplicito di condivisione.

## Fase 0 — fatta il 2026-08-27

Due difetti trovati dal panel nel codice esistente, corretti prima del resto perche' valgono
comunque, qualunque strada si prenda:

1. `MainViewModel` rileggeva la configurazione **solo all'avvio**. Una finestra gia' collegata
   che riceveva 401 restava bloccata su "Token rejected" fino al riavvio. Corretto, con test.
2. Il token del client stava in `%APPDATA%` **Roaming**, che su una macchina di dominio si
   sincronizza con un file server. Spostato in `LocalApplicationData`.

Piu' un prerequisito emerso dalla campagna di misure e gia' chiuso:

3. `ServizioInMemoria` impostava tre variabili d'ambiente e non le rimuoveva mai. Innocuo con
   una sola classe che usa la fixture, velenoso appena il canale locale costruira' host Kestrel
   veri nello stesso assembly. Ripristinate all'uscita, e le classi che toccano stato globale
   del processo messe in una collezione xunit comune.

## I sei piani

Ognuno deve produrre software funzionante e testabile da solo. In ordine di dipendenza.

### 1. Canale locale nel servizio

Due ascolti in un solo Kestrel: TCP come oggi, piu' named pipe su Windows e socket unix su
Linux. **Misurato: i due trasporti convivono davvero** in un unico host, e gli stessi endpoint
minimal-API rispondono su entrambi.

Trappole verificate eseguendo codice, tutte capaci di far fallire il piano in silenzio:

- **`UseNamedPipes()` non serve** per aprire una pipe: su Windows il trasporto e' gia'
  registrato, basta `ListenNamedPipe`. Serve solo per impostare le opzioni, ed e' bene saperlo
  perche' `UseNamedPipes` e' marcato Windows-only e fa fallire la build (vedi CA1416 sotto).
- **`CurrentUserOnly` vale `true` di default**, cioe' "solo l'account che esegue il server":
  l'opposto di cio' che serve, dato che il servizio gira come LocalSystem e la GUI come utente
  interattivo.
- **`PipeSecurity` e `CurrentUserOnly = false` vanno impostate INSIEME nella stessa callback.**
  Impostare solo la prima fa lanciare all'avvio `ArgumentException: 'pipeSecurity' must be null
  when 'options' contains 'PipeOptions.CurrentUserOnly'` — rumoroso, quindi innocuo. Impostare
  solo la seconda e' il caso pericoloso: **l'host parte normalmente e produce una pipe con DACL
  `(A;;FR;;;WD)(A;;FR;;;AN)`, cioe' leggibile da Everyone e da ANONYMOUS LOGON.** Degrado
  silenzioso, nessun test di compilazione lo prende.
- **La DACL deve dare all'account che ospita la pipe `FullControl`**, non il solo
  `CreateNewInstance`. La prima istanza si crea sempre; e' dalla **seconda** che serve il bit
  `FILE_CREATE_PIPE_INSTANCE` (0x4), e Kestrel ne apre piu' d'una. Senza, il bind fallisce con
  `UnauthorizedAccessException` che Kestrel traduce nel fuorviante `address already in use`.
- **L'ordine delle ACE non va gestito a mano** se si costruisce il descrittore con
  `AddAccessRule`: `PipeSecurity` canonicalizza da sola, e una DENY aggiunta per ultima finisce
  comunque in testa (verificato confrontando le due SDDL, identiche carattere per carattere).
  Ma la garanzia e' del tipo `CommonAcl`, **non** della nostra chiamata: importando un
  descrittore da SDDL o da forma binaria la DENY resta dove sta e diventa inerte. Regola:
  costruire sempre con `AddAccessRule`, mai importare.
- **La guardia va registrata PRIMA del middleware del bearer token.** Con il token davanti, la
  guardia non viene mai eseguita.
- Su Linux serve `Socket.GetRawSocketOption(1, 17, ...)` per leggere `SO_PEERCRED`, perche' la
  mappatura .NET di quell'opzione non esiste. Valori giusti su Linux: `SOL_SOCKET = 1`,
  `SO_PEERCRED = 17`, `struct ucred = { int32 pid; uint32 uid; uint32 gid; }`, 12 byte.
- **Il limite del percorso di un socket unix e' 107 byte, non 108.** Il messaggio d'errore di
  .NET dice `must be between 1 and 108 characters` e mente: non conta il terminatore. Una
  guardia scritta a 108 lascia passare esattamente il caso di confine, che e' l'unico che conta.
  E il conteggio e' in **byte UTF-8**, non in caratteri.
- **`Directory.CreateDirectory(percorso, modo)` non applica il modo a una directory che esiste
  gia'.** E' un no-op silenzioso. Quindi una difesa "directory 0700" non fa niente dal secondo
  avvio in poi, ne' su una `/run/observer` creata da systemd. Serve un `SetUnixFileMode`
  esplicito dopo la creazione.
- **Il modo di default del file socket non basta**: `connect(2)` su AF_UNIX richiede il bit di
  **scrittura**, non di lettura.
- **Il file del socket viene cancellato da .NET su una chiusura pulita, su Windows e su Linux
  allo stesso modo** (`UnixDomainSocketEndPoint` porta un `boundFileName` e `Dispose` fa
  l'unlink). La bonifica serve solo dopo una morte violenta. L'idea diffusa che su Linux il file
  sopravviva sempre e' falsa sotto .NET.
- **La bonifica ingenua "se il file esiste, cancellalo" permette a una seconda istanza di
  rubare il socket a una istanza viva.** Serve un probe, ma con `ConnectAsync` e un timeout
  esplicito: un `Connect()` bloccante contro un listener vivo con la coda di accept piena resta
  appeso indefinitamente, e sotto systemd diventa un timeout di avvio senza diagnosi.
- **Un URL di endpoint scritto male non fallisce: fallisce peggio.** Un percorso di socket
  Windows dentro `http://unix:...` fa legare a Kestrel `[::]:80` **su tutte le interfacce**,
  senza eccezione ne' warning. Si crede di aver aperto un canale privato e si e' aperta la LAN.
  Serve una convalida esplicita degli URL degli endpoint accanto a `storage.Validate()`.
- **CA1416, con `TreatWarningsAsErrors`, fa fallire la build su ENTRAMBI i runner** — e'
  analisi statica, non dipende dall'OS che compila. Forma verificata a zero avvisi: ogni classe
  che tocca `PipeSecurity`, `SecurityIdentifier`, `WindowsIdentity` o `RunAsClient` porta il suo
  `[SupportedOSPlatform("windows")]`, e ogni sito di chiamata da codice cross-platform sta
  dentro `if (OperatingSystem.IsWindows())`. **L'attributo su una local function non funziona e
  non copre il corpo di una lambda**, e una guardia estratta in una proprieta' di comodo non
  viene seguita dall'analyzer. Conseguenza pratica: `Program.cs` e' fatto di top-level
  statements, quindi il cablaggio della pipe **non puo' stare li'**.

### 2. Autorizzazione a tabella

Le venti righe di `app.Use` in `Program.cs` diventano una **funzione pura** con negazione
predefinita, verificabile con un test a tabella su entrambi i runner invece che avviando il
servizio. Forma misurata funzionante: otto casi, sette rifiuti, e il valore zero dell'enum e'
un rifiuto — cosi' anche una struct non inizializzata nega.

La funzione non deve calcolare la lista degli ammessi da `WindowsIdentity.GetCurrent()`: il
servizio gira come LocalSystem, e una lista costruita cosi' chiude fuori l'unico client
legittimo. Deve confrontare **SID**, mai nomi tradotti: al livello `Identification` — il minimo
che si vuole accettare — `Translate(typeof(NTAccount))` fallisce con
`UnauthorizedAccessException` su ogni SID, e anche `AuthenticationType` non e' leggibile.

Il `404` sugli endpoint di pairing quando la richiesta arriva dalla rete e' deliberato: chi
ruba il token non deve poter ruotare le chiavi per chiudere fuori il proprietario, e un 404 non
conferma nemmeno che l'endpoint esista.

### 3. Deposito del token, pairing e CLI

`credentials.json` sotto `C:\ProgramData\Observer\` o `/etc/observer/`, scrittura atomica,
token corrente piu' precedente con scadenza per non tagliare fuori i client durante una
rotazione. Verbi da riga di comando: `share`, `rotate-key`, `show-key`, `doctor`.

La CLI non e' un accessorio: e' l'unico modo di prendere il token da una macchina **senza
schermo**, ed e' il motivo per cui questa proposta ha superato l'obiezione del terzo giudice.

Il nuovo progetto eseguibile **deve avere il suo `runtimeconfig.template.json`** con
`System.Globalization.Invariant: true`, come gia' lo hanno Service e App: senza, su Linux non
parte per mancanza di ICU. Lo prescrive `Directory.Build.props`. E va registrato in
`Observer.slnx`.

### 4. Client e interfaccia macchine

`ObserverEndpoint` (locale oppure remoto), `SocketsHttpHandler.ConnectCallback` verso pipe o
socket, elenco macchine in una barra laterale, `client.json` che diventa `machines.json`.

Misurato: un `HttpClient` parla su named pipe senza problemi, e **l'host nell'URI e'
arbitrario** — finisce solo nell'header `Host`, il DNS non viene interpellato. Quindi il codice
di chiamata resta uno solo e cambia soltanto l'handler.

Il costo dichiarato in origine era sbagliato: non sono solo le dieci prove di
`ClientConfigurationTests`. Rendere il token facoltativo in `ObserverClientOptions` —
necessario, sul canale locale non c'e' — rompe la **compilazione** dell'helper usato da tutti e
nove i test di `MetricsClientTests`. Il conto e' 10 + 9. E il test che asserisce la presenza
dell'header `Authorization` va sdoppiato: sul canale locale quell'header non deve esserci.

`Observer.App` oggi non ha nulla di Windows-specifico. `NamedPipeClientStream` e
`System.Security.Principal` ci portano dentro CA1416: stessa forma del piano 1.

### 5. HTTPS sul percorso remoto

Certificato autofirmato per macchina, impronta fissata in `machines.json`, il comando di
condivisione trasporta anche l'impronta. Da progettare: rinnovo del certificato e cosa succede
quando l'impronta cambia.

Perche' non e' rimandabile: oggi sul percorso remoto **il token attraversa la rete in chiaro una
volta al secondo**. Una singola cattura di pacchetti consegna una credenziale permanente, e
ruotarla non aiuta perche' quella nuova e' sul filo un secondo dopo.

### 6. Packaging

MSI con WiX su Windows, `.deb` con unit systemd su Linux, piu' un job `pack` in CI.

**Due regole che valgono per entrambi.** L'installer non genera, non scrive e non conosce alcun
token: non ha segreti da proteggere, da registrare nel log MSI o da lasciarsi dietro se
fallisce a meta'. Ed entrambi verificano di aver funzionato interrogando il servizio dal canale
locale prima di dichiarare successo — cosa che non richiede alcun token.

Con una correzione: l'installer gira come SYSTEM o come root, quindi una verifica fatta con la
**sua** identita' passerebbe anche con una DACL che esclude l'utente desktop, cioe' proprio il
guasto che il piano 1 rischia. La verifica va fatta con l'identita' dell'utente che ha lanciato
l'installazione.

**Non rinominare il job `build` ne' i valori della matrice `os`** in `build.yml`: i due contesti
richiesti dal ruleset `main-protection` sono le stringhe esatte `build (ubuntu-latest)` e
`build (windows-latest)`, e un disallineamento lascia ogni PR in attesa per sempre.

Nota: `.gitignore` ignora `*.msi`, quindi il job `pack` dovra' pubblicare artefatti, non
committarli.

## Costo, senza ammorbidirlo

Circa **1700-1900 righe di C#** piu' **~400 di packaging**, piu' **~250 per HTTPS**. E' la
strada piu' cara fra quelle valutate: se il criterio fosse solo il tempo, vincerebbe l'installer
che scrive semplicemente il token nei due file.

La stima originale **non comprendeva** tre voci intere, che non sono un aggiustamento ma roba
assente dall'elenco: la guardia sull'identita' con il suo banco di prova, il banco nuovo capace
di avviare host Kestrel veri (nessuno dei 201 test attuali ne avvia uno), e i nove test di
`MetricsClientTests`.

## Rischi accettati

- **Kestrel su named pipe e' una strada poco battuta.** Verificato: ne' Stack Overflow ne'
  l'archivio SOFA hanno un solo risultato su `ListenNamedPipe`, `IConnectionNamedPipeFeature` o
  la lettura di `SO_PEERCRED` da .NET. Ogni problema incontrato li' andra' risolto da zero.
- **Un endpoint che non si binda abbatte l'INTERO host**, endpoint TCP compreso. Oggi due
  istanze collidono solo sulla porta 5057; dopo, anche sul nome della pipe e su un socket
  orfano lasciato da un crash. Da mitigare rendendo nome e percorso configurabili.
- **Appena esiste un endpoint pipe o unix, `ASPNETCORE_URLS` e l'`applicationUrl` di
  `launchSettings.json` smettono di funzionare** (Kestrel emette `Overriding address(es)`). La
  sezione `Kestrel` di `appsettings.json` diventa un prerequisito, non un dettaglio.
- **Nessuno dei 201 test tocca un trasporto reale.** `WebApplicationFactory` sostituisce Kestrel
  con un `TestServer` in memoria, quindi la sezione `Kestrel` di `appsettings.json` non viene
  mai analizzata: un URL di endpoint sbagliato passa la CI verde. Il banco del piano 1 deve
  avviare Kestrel davvero.
- **Un token per macchina, non per peer.** Revocare l'accesso a un solo computer li revoca
  tutti. La strada giusta sarebbe un registro dei client, ed e' fuori da questo lavoro.
- **Due canali significano due percorsi di autorizzazione**, e prima o poi qualcuno aggiungera'
  un endpoint pensando a uno solo. La negazione predefinita lo fa cadere nel 401 invece che nel
  "passa", che e' il verso giusto in cui sbagliare, ma resta una superficie doppia.
- **L'MSI non firmato fa comparire l'avviso SmartScreen.** La prima cosa che si vedrebbe
  installando un programma di monitoraggio sarebbe un allarme di sicurezza. Un certificato di
  code signing e' un costo ricorrente reale.
- **Un installer e' una superficie nuova**: registra un servizio, crea un utente, tocca un
  firewall, scrive in `ProgramData` e in `/etc`. Puo' rompere una macchina in modi che due
  `dotnet run` non potevano, e la CI compila il codice, non l'installazione.

## Cosa resta da misurare prima che il piano 1 sia completo

Elencato perche' sono lacune note, non perche' siano state dimenticate.

1. **Servizio installato sotto LocalSystem con la GUI dell'utente interattivo.** Nessuna delle
   sessioni di misura era amministratore, quindi questa combinazione — che e' la configurazione
   di produzione — non e' mai stata eseguita. Regge su un'inferenza fra due misure vere.
2. **Un chiamante SMB da una SECONDA macchina.** Verso se' stessa, Windows restituisce il token
   interattivo originale: questa macchina non e' un banco di prova valido per la difesa basata
   sul SID `NETWORK`. Il discriminante `GetNamedPipeClientComputerName` non ne ha bisogno, ma la
   DACL si', e va provata da un secondo host.
3. **`dotnet run` come utente normale su Linux**, dove `/run/observer` non e' creabile: senza un
   ripiego documentato il piano 1 rende il servizio non avviabile in sviluppo su meta' della CI.
4. **Riconnessione quando il servizio si riavvia e la pipe sparisce.** E' il caso normale di un
   aggiornamento, `MainViewModel` ha gia' un percorso di riconnessione da rispettare, e nessuna
   misura lo ha esercitato.

## Condizione di rivalutazione

Il vincolo che regge tutta la difesa del canale locale — *"tanto CPU e memoria le vede gia'
Gestione attivita'"* — **scade il giorno in cui arriva l'elenco processi**: su Windows un utente
standard non legge la riga di comando dei processi altrui, e le righe di comando possono
contenere segreti. Quando quel collector verra' aggiunto, la decisione sull'accesso locale va
riaperta.
