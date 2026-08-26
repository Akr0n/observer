# Token, canale locale e installer — specifica e suddivisione

> **Stato:** deciso il 2026-08-27, non ancora implementato (salvo la Fase 0, gia' fatta).
> Questo documento NON e' un piano eseguibile: e' la specifica e la suddivisione in sei piani.
> Ogni piano va scritto quando lo si affronta, con la skill `superpowers:writing-plans`.

## La decisione

Sulla macchina locale **il sistema operativo sa gia' chi sta chiamando**, quindi un segreto
condiviso e' lo strumento sbagliato. Il bearer token resta, ma solo per le connessioni remote.

- **Windows**: named pipe `\\.\pipe\Observer`, chiamante identificato dal SID.
- **Linux**: socket unix `/run/observer/observer.sock`, chiamante identificato da `SO_PEERCRED`.
- **Rete**: `0.0.0.0:5057` con bearer token, come oggi, **piu' HTTPS** (vedi piano 5).

### Perche' questa e non le alternative

Due delle tre proposte valutate SPOSTAVANO il segreto in un posto piu' comodo (un deposito
consegnato a richiesta, un file leggibile da un gruppo). Questa lo ELIMINA dal caso comune.

La differenza pratica: con le altre, ogni utente della macchina ha in mano una credenziale
valida **anche dalla rete**, portatile, permanente e non revocabile singolarmente. Con questa
non ha niente da portare via, e `Token rejected` in locale diventa un errore che non puo' piu'
accadere perche' non c'e' piu' nessun token da sbagliare.

Il canale locale resta **HTTP/1.1**: `MetricsClient`, `ServiceOutcome`, il timeout a 3 secondi,
il 503 che diventa "Service is starting" e il controllo su `MachineSnapshot.CurrentSchemaVersion`
continuano a valere identici sui due canali. Non nasce un secondo protocollo da mantenere.

## Le decisioni prese da Federico

| Domanda | Scelta |
| --- | --- |
| Come si autentica il client locale | **Identita' del sistema operativo**, non un token |
| HTTPS sul percorso remoto | **Adesso**, insieme al resto |
| Portata | Progetto completo, non la versione minima |

Restano da decidere quando si arrivera' al piano corrispondente:

- **Chi legge le metriche in locale senza token**: chiunque abbia una sessione sulla macchina,
  oppure solo gli amministratori. L'argomento "tanto CPU e memoria le vede gia' Gestione
  attivita'" e' vero per il PRESENTE e falso per lo STORICO: novanta giorni di grafici dicono a
  che ore una macchina viene usata.
- **Regola firewall su Windows**: attiva subito, oppure solo dopo un gesto esplicito di
  condivisione.

## Fase 0 — fatta il 2026-08-27

Due difetti trovati dal panel nel codice esistente, corretti prima del resto perche' valgono
comunque, qualunque strada si prenda:

1. `MainViewModel` rileggeva la configurazione **solo all'avvio**. Una finestra gia' collegata
   che riceveva 401 restava bloccata su "Token rejected" fino al riavvio. Corretto, con test.
2. Il token del client stava in `%APPDATA%` **Roaming**, che su una macchina di dominio si
   sincronizza con un file server. Spostato in `LocalApplicationData`.

## I sei piani

Ognuno deve produrre software funzionante e testabile da solo. In ordine di dipendenza.

### 1. Canale locale nel servizio

Due ascolti in un solo Kestrel: TCP come oggi, piu' named pipe su Windows e socket unix su Linux.

Due trappole note. `NamedPipeTransportOptions.CurrentUserOnly` vale `true` di default, cioe'
"solo l'account che esegue il server" — l'opposto di cio' che serve, dato che il servizio gira
come LocalSystem e la GUI come utente interattivo: va messo a `false` e sostituito con una
`PipeSecurity` esplicita. E su Linux serve `Socket.GetRawSocketOption(1, 17, ...)` per leggere
`SO_PEERCRED`, perche' la mappatura .NET di quell'opzione non esiste.

### 2. Autorizzazione a tabella

Le venti righe di `app.Use` in `Program.cs` diventano una **funzione pura** con negazione
predefinita, verificabile con un test a tabella su entrambi i runner invece che avviando il
servizio.

Il `404` sugli endpoint di pairing quando la richiesta arriva dalla rete e' deliberato: chi
ruba il token non deve poter ruotare le chiavi per chiudere fuori il proprietario, e un 404 non
conferma nemmeno che l'endpoint esista.

### 3. Deposito del token, pairing e CLI

`credentials.json` sotto `C:\ProgramData\Observer\` o `/etc/observer/`, scrittura atomica,
token corrente piu' precedente con scadenza per non tagliare fuori i client durante una
rotazione. Verbi da riga di comando: `share`, `rotate-key`, `show-key`, `doctor`.

La CLI non e' un accessorio: e' l'unico modo di prendere il token da una macchina **senza
schermo**, ed e' il motivo per cui questa proposta ha superato l'obiezione del terzo giudice.

### 4. Client e interfaccia macchine

`ObserverEndpoint` (locale oppure remoto), `SocketsHttpHandler.ConnectCallback` verso pipe o
socket, elenco macchine in una barra laterale, `client.json` che diventa `machines.json`.

Le dieci prove di `ClientConfigurationTests` vanno riscritte: cambia l'ordine di risoluzione.

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

**Non rinominare il job `build` ne' i valori della matrice `os`** in `build.yml`: i due contesti
richiesti dal ruleset `main-protection` sono le stringhe esatte `build (ubuntu-latest)` e
`build (windows-latest)`, e un disallineamento lascia ogni PR in attesa per sempre.

## Costo, senza ammorbidirlo

Circa **1700-1900 righe di C#** piu' **~400 di packaging**, piu' **~250 per HTTPS**. Dieci test
di configurazione da riscrivere e tre livelli di copertura nuovi. E' la strada piu' cara fra
quelle valutate: se il criterio fosse solo il tempo, vincerebbe l'installer che scrive
semplicemente il token nei due file.

## Rischi accettati

- **Kestrel su named pipe e' una strada poco battuta.** Le API sono di prima parte e sono state
  verificate presenti nel ref pack 10.0.11 di questa macchina, ma i problemi che si incontrano
  li' hanno molte meno risposte in rete di quelli su HTTP normale.
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

## Condizione di rivalutazione

Il vincolo che regge tutta la difesa del canale locale — *"tanto CPU e memoria le vede gia'
Gestione attivita'"* — **scade il giorno in cui arriva l'elenco processi**: su Windows un utente
standard non legge la riga di comando dei processi altrui, e le righe di comando possono
contenere segreti. Quando quel collector verra' aggiunto, la decisione sull'accesso locale va
riaperta.
