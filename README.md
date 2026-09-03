# Observer

![build](https://github.com/Akr0n/observer/actions/workflows/build.yml/badge.svg)

Dashboard cross-platform per il monitoraggio dei parametri vitali della macchina
e dei dispositivi presenti sulla rete locale. Gira su Windows e Linux.

> **Stato:** funzionante e installabile. Il servizio campiona una volta al secondo su Windows
> e su Linux - CPU, memoria, spazio per volume, attivita' per disco - conserva le serie su
> SQLite, si genera da solo il proprio token di macchina, ed espone i dati sia sulla rete sia
> su un canale locale che non richiede credenziali. Il client desktop li mostra dal vivo, con
> un'ora di storico sotto i quadranti, e dal quadrante della CPU o della memoria apre l'elenco
> dei processi che la stanno consumando, da cui un processo si puo' terminare. Ci sono un
> pacchetto MSI per Windows e un `.deb` per Linux, che registrano il servizio e installano
> la dashboard. Mancano la rete e i sensori di temperatura.
>
> L'interfaccia dell'applicazione è in **inglese**; questa documentazione e i commenti
> nel codice restano in italiano.

## Architettura

Il progetto è diviso in un servizio headless e un client desktop: su Windows i
servizi girano in Session 0 e non possono mostrare un'interfaccia grafica, quindi
raccolta e visualizzazione devono essere due processi distinti.

| Progetto | Ruolo |
| --- | --- |
| `src/Observer.Core` | Modelli condivisi, astrazione dei collector e adattatori di piattaforma |
| `src/Observer.Service` | Servizio headless: campiona a 1 Hz, conserva le serie su SQLite con aggregazione ed espone i dati via HTTP autenticato |
| `src/Observer.App` | Client desktop Avalonia: si collega al servizio e mostra le metriche dal vivo |
| `src/Observer.Cli` | Riga di comando `observer`: condivide la chiave, la ruota, diagnostica, e custodisce i token delle altre macchine |
| `tests/Observer.Core.Tests` | Test su `Observer.Core` |
| `tests/Observer.Service.Tests` | Test su `Observer.Service`, storico e canale locale compresi |
| `tests/Observer.App.Tests` | Test sul client HTTP e sulla traduzione delle risposte |
| `tests/Observer.Cli.Tests` | Test sui messaggi della riga di comando |

Il client può puntare al servizio in esecuzione sulla stessa macchina o su un'altra.

### Cosa misura

| Metrica | Un'istanza e' | Note |
| --- | --- | --- |
| CPU | la macchina | percentuale di utilizzo, dal delta dei tempi di sistema |
| Memoria | la macchina | usata, disponibile e totale; "disponibile" e' una stima quando il sistema la fornisce come tale, e lo dice |
| Spazio disco | un volume (`C:`, `/`) | usato, libero e totale; una capacita' pari a zero e' "sconosciuta", non "vuota" |
| Attivita' disco | un dispositivo (`Disk 0`, `sda`) | byte letti e scritti al secondo, e percentuale di tempo occupato |

Le istanze dell'attivita' disco sono **dispositivi**, non volumi, e di proposito non coincidono
con quelle dello spazio: un disco porta piu' volumi e un volume puo' estendersi su piu' dischi,
quindi attribuire il traffico di due volumi a una lettera sarebbe peggio di un nome di
dispositivo onesto. La percentuale di occupazione si ricava dal tempo **inattivo**, mai sommando
tempo di lettura e di scrittura: i due si sovrappongono, e su una finestra misurata la somma
dava 843%.

Oltre alle metriche, il servizio espone l'**elenco dei processi** ordinato per CPU o per
memoria, ed e' quello che la dashboard apre cliccando il quadrante corrispondente. I quadranti
dei dischi non lo aprono: lo spazio occupato su un volume non si attribuisce a un processo in
esecuzione.

### Aggiungere una metrica

L'unico punto di estensione è `IMetricCollector`. Ogni collector pubblica i propri
`MetricDescriptor` e restituisce una lista di `MetricPoint`, in un formato uguale per
tutte le sorgenti. La dimensione per istanza — il core, il disco, l'interfaccia di rete —
è un campo stringa del punto, non una gerarchia di tipi: per questo per-disco e
per-processo passano dalla stessa interfaccia senza modificarla. Le unità di misura sono
un tipo aperto, quindi un sensore in `rpm` o in `V` non richiede di toccare il Core.

In pratica si scrive una classe nuova, ma i file da toccare sono cinque, e vale la pena
saperlo prima:

| file | perche' |
|---|---|
| `src/Observer.Core/Metrics/<Nome>/<Nome>Collector.cs` | il collector |
| `src/Observer.Core/Composition/ObserverMetrics.cs` | la registrazione |
| `src/Observer.Core/Platform/HostPlatform.cs` | quale provider su quale sistema |
| `src/Observer.Core/Platform/Windows/WindowsProviders.cs` | come si misura su Windows |
| `src/Observer.Core/Platform/Linux/LinuxProviders.cs` | come si misura su Linux |

Piu' la tabella dei titoli leggibili in `src/Observer.App/Services/SnapshotProjection.cs`,
senza la quale il riquadro si intitola `disk` invece di `Disk`.

Quello che **non** va toccato e' l'interfaccia: `IMetricCollector` regge una sorgente nuova
cosi' com'e', e le due righe che contano - la dimensione per istanza come campo del punto e
l'unita' come tipo aperto - sono cio' che lo rende vero.

Una metrica non misurabile su una piattaforma **resta nel catalogo** e si dichiara
`Unsupported` con il motivo, invece di sparire: "non si può misurare qui" e "me la sono
dimenticata" devono restare distinguibili in dashboard.

Lo stesso vale **per singola istanza**. Un punto si costruisce solo dalle fabbriche
`MetricPoint.Measured`, `.Unsupported` o `.Unavailable`, e porta con sé il proprio stato e
il proprio motivo. Serve per il caso normale di una sorgente multi-istanza: tre dischi di
cui uno dietro un bridge USB che non inoltra i comandi SMART deve poter riportare i due
dischi sani **e** la spiegazione per il terzo. Un collector che legge più istanze deve
quindi emettere un punto per ognuna, comprese quelle fallite — omettere l'istanza significa
"non applicabile", non "non ci sono riuscito".

### Vincolo sul campionamento

**Un solo `BackgroundService` campiona.** Gli endpoint HTTP leggono l'ultimo snapshot
dalla cache e non chiamano mai `CollectAsync`. Non è una scelta di prestazioni: il
collector della CPU conserva il campione precedente, e due raccolte simultanee
produrrebbero percentuali sbagliate in modo intermittente e plausibile.

### Storico e rollup

Il servizio conserva le serie su SQLite con tre livelli di dettaglio: il campione grezzo a
1 s, l'aggregato a 1 minuto e quello a 5 minuti. Senza aggregazione il file crescerebbe
senza limite.

Ogni bucket conserva **somma e conteggio**, non la media. Ricombinando bucket con un numero
diverso di campioni — caso normale dopo un riavvio o il timeout di un collector — la media
delle medie darebbe un numero credibile e falso.

I valori predefiniti, tutti modificabili in `appsettings.json` sotto `Observer:Storage`:

| Parametro | Predefinito | Cosa copre |
| --- | --- | --- |
| `RawRetention` | 6 ore | il dettaglio al secondo |
| `MinuteRetention` | 7 giorni | "la settimana scorsa a quest'ora" |
| `FiveMinuteRetention` | 90 giorni | l'andamento di lungo periodo |
| `Enabled` | `true` | a `false` il servizio si comporta come se lo storico non esistesse |

Un dato non viene mai cancellato prima di essere stato aggregato, anche se la ritenzione lo
permetterebbe. Un punto mancante resta mancante e non diventa mai uno zero: in un grafico
uno zero è un dato, un buco è un buco.

### Endpoint

Un chiamante **locale identificato** li raggiunge tutti **senza alcun token**: sulla macchina
il sistema operativo sa gia' chi sta chiamando, e un segreto condiviso sarebbe lo strumento
sbagliato. Dalla **rete** il bearer token resta obbligatorio.

| Endpoint | Cosa restituisce |
| --- | --- |
| `GET /metrics/catalog` | le metriche esistenti, con nome leggibile e unità |
| `GET /metrics/latest` | l'ultimo campionamento |
| `GET /metrics/series` | quali serie sono state davvero misurate su questa macchina |
| `GET /metrics/history` | i punti storici; `resolution` accetta `auto`, `raw`, `1m`, `5m` |
| `GET /metrics/storage` | dove scrive, quanto occupa, fin dove ha aggregato |
| `GET /processes` | i processi che consumano di piu'; `by` accetta `cpu` (predefinito) o `memory`, `top` da 1 a 100 (predefinito 15) |
| `POST /processes/{pid}/kill` | termina quel processo: `204` se e' andata, `404` se il pid non esiste |

`auto` sceglie la risoluzione più fine ancora disponibile per l'intervallo richiesto: il
grezzo di ieri è stato cancellato, e restituire un grafico vuoto si leggerebbe come
"macchina non monitorata".

`/processes/{pid}/kill` e' l'**unica scrittura** del servizio, ed e' ammessa dalla rete col
token per scelta esplicita: da un'altra macchina si vede un processo impazzito e lo si ferma
da li'. Ogni tentativo - riuscito o rifiutato dal sistema operativo - finisce nel log del
servizio con pid, nome e provenienza del chiamante. E' anche il motivo per cui il token non
sta piu' in un file (vedi "Guardare un'altra macchina"). `GET /processes` risponde `503`
quando l'elenco non si puo' leggere su quella macchina.

## Requisiti

- .NET SDK 10.0
- Windows 10/11 oppure una distribuzione Linux con ambiente grafico

## Sviluppo

```bash
dotnet build
```

```bash
dotnet test
```

**Non serve configurare niente.** Il servizio ascolta in HTTPS su `0.0.0.0:5058` e, in piu', apre un
canale locale — una named pipe su Windows, un socket unix su Linux — su cui un chiamante
locale identificato entra senza credenziali. Il token di macchina, che serve solo perche' un
ALTRO computer possa interrogare questo, se lo genera il servizio al primo avvio e se lo
custodisce sotto `C:\ProgramData\Observer` oppure `/etc/observer`, con permessi che
escludono ogni altro account.

Avvio di servizio e client, in due terminali separati:

```bash
dotnet run --project src/Observer.Service
```

```bash
dotnet run --project src/Observer.App
```

La dashboard non ha bisogno di sapere niente: senza configurazione va sul canale locale della
macchina su cui gira.

Per leggere le metriche **dalla rete** serve invece il token di quella macchina, che si ottiene
su quella macchina, da un terminale amministrativo:

```bash
observer share
```

`observer share` stampa **due** valori, e servono entrambi: il token dice che chi chiama e'
autorizzato, l'impronta del certificato dice che quella macchina e' chi dichiara di essere.
Senza la seconda, chi riesce a mettersi in mezzo presenta il proprio certificato e il token
gli arriva addosso.

Il modo normale di usarli e' la dashboard: indirizzo e impronta vanno in `machines.json`, il
token nel deposito di questa macchina con `observer token set` (vedi "Guardare un'altra
macchina"), e l'impronta la confronta lei. Da riga di comando il certificato e' autofirmato,
quindi `curl` non ha un'autorita' a cui appoggiarsi: l'impronta va confrontata **a mano**, e
solo dopo si procede.

```bash
# 1. che impronta presenta quella macchina, vista da qui
openssl s_client -connect la-macchina:5058 </dev/null 2>/dev/null   | openssl x509 -noout -fingerprint -sha256

# 2. se e SOLO se coincide con quella stampata da "observer share" la':
curl --insecure -H "Authorization: Bearer $Observer__ApiToken"   https://la-macchina:5058/metrics/latest
```

`--insecure` disattiva ogni verifica, quindi da solo non va mai usato: qui vale perche' il
passo 1 ha gia' fatto a mano il controllo che conta.

### Riga di comando

Dopo l'installazione con l'MSI, `observer` e' gia' nel `PATH` di sistema: basta aprire un
terminale **nuovo**. Senza installare, l'eseguibile va invocato col percorso, e in PowerShell
serve l'operatore di chiamata `&` - un percorso fra virgolette a inizio riga per PowerShell e'
una stringa, non un comando, e il tentativo ovvio fallisce con un errore di sintassi che non
nomina la propria causa.

| Verbo | Elevazione | Cosa fa |
| --- | --- | --- |
| `observer share` | si | mostra il token di macchina e l'impronta, per configurare un ALTRO computer |
| `observer rotate-key` | si | genera una chiave nuova; la precedente vale ancora 24 ore, e il servizio usa la vecchia finche' non viene riavviato |
| `observer doctor` | no | dove sta il deposito, com'e' protetto, e se il canale locale risponde |
| `observer token set NOME` | no | custodisce il token di un'ALTRA macchina; lo legge da standard input e non lo mostra |
| `observer token forget NOME` | no | dimentica quel token |

### Guardare un'altra macchina

La macchina su cui sei seduto non richiede nulla: la dashboard entra dal canale locale, senza
porta e senza token. Per guardarne un'altra servono **due** valori, e fanno lavori diversi.

```bash
observer share
```

su **quella** macchina, da un terminale con privilegi, stampa il token e l'impronta del suo
certificato. Il token dice che il chiamante puo' entrare; l'impronta dice che la macchina e'
quella che dichiara di essere.

L'impronta e l'indirizzo vanno in `machines.json`, accanto a `client.json`. **Il token no:**

```json
{
  "machines": [
    {
      "name": "portatile",
      "baseAddress": "https://portatile:5058/",
      "fingerprint": "sha256:..."
    }
  ]
}
```

`name` e' **obbligatorio**: e' la chiave sotto cui viene custodito il token, e viene controllato
prima di comporre qualsiasi percorso - lettere, cifre, spazio, `.`, `_` e `-`, niente altro -
perche' un nome come `../../id_rsa` andrebbe altrimenti a leggere e sovrascrivere un file fuori
dalla cartella.

Il token si consegna a questa macchina con un comando, e non si scrive da nessuna parte:

```bash
observer token set portatile
```

Lo legge da standard input e non lo mostra mentre lo digiti. Finisce nel **Credential Manager
di Windows**, oppure — su Linux — in un file leggibile solo dal proprietario, che Observer si
rifiuta di usare se i permessi sono piu' larghi.

Il motivo e' cambiato di recente e vale la pena dirlo: da quando esiste
`/processes/{pid}/kill`, quel token non serve piu' solo a **leggere** la CPU di un'altra
macchina, serve anche a **fermarci dei processi**. Un file fatto per essere aperto, copiato e
incollato non e' il posto giusto per una credenziale del genere, e infatti una voce che se lo
porta ancora dietro viene rifiutata — anche quando il token e' quello giusto.

**Aggiornando da una versione precedente alla 0.6.0**: per ogni macchina remota esegui
`observer token set NOME` e poi cancella la riga `apiToken` da `machines.json`. Finche' resta,
quella macchina compare sotto l'elenco come inutilizzabile, con scritto il comando da eseguire.

La **barra laterale c'e' sempre**, anche quando la macchina e' una sola: li' dentro trovi il
percorso esatto di `machines.json` da scrivere. Nasconderla finche' non ci sono due macchine
significherebbe annunciare la funzione solo a chi sa gia' che esiste.

Quella locale e' sempre la prima: non si elenca e non si puo' togliere. Una voce scritta male
**non sparisce in silenzio** - compare sotto l'elenco con il motivo, perche' una macchina che
semplicemente non c'e' e' indistinguibile da una che non e' stata aggiunta.

Quando una macchina non risponde, la barra di stato distingue **connessione rifiutata** - c'e'
qualcuno a quell'indirizzo ma il servizio non e' in ascolto: va avviato - da **nessuna risposta**
entro 8 secondi, che di solito e' una porta chiusa o un firewall. I due rimedi sono opposti, e
confonderli costa un pomeriggio. Nei primi 10 secondi la barra resta gialla, non rossa: un
servizio che sta ancora partendo rifiuta anche lui. Un token rifiutato, un'impronta diversa o
un servizio piu' vecchio della dashboard sono rossi da subito, perche' fra un minuto saranno
identici.

**Sulla rete il servizio risponde solo in HTTPS.** Prima rispondeva in chiaro, e il token
attraversava la rete una volta al secondo: una sola cattura di pacchetti consegnava una
credenziale permanente, e ruotarla non serviva perche' quella nuova era sul filo un secondo
dopo. Il certificato e' autofirmato e generato dal servizio stesso, quindi nessuna autorita' lo
garantisce: **e' l'impronta a legare il collegamento a quella macchina**, ed e' per questo che
senza non si va da nessuna parte. Se un giorno cambia, la dashboard si ferma e mostra la vecchia
e la nuova. Dopo una reinstallazione e' normale e si aggiorna il file; se non hai reinstallato
niente, non copiare il valore nuovo.

### Pacchetti

```bash
./packaging/windows/pack.ps1
```

```bash
./packaging/linux/pack.sh
```

Il primo produce un MSI, il secondo un `.deb`. Registrano il servizio, installano la dashboard
e creano il collegamento nel menu.

**Disinstallando l'MSI dal Pannello di controllo se ne va tutto**: il servizio, i file, il
deposito delle credenziali sotto `ProgramData` e lo storico, che vive nel profilo dell'account
di sistema e non e' un posto che qualcuno andrebbe a cercare a mano. Un **aggiornamento** e'
escluso da questa pulizia, e la distinzione non e' una sottigliezza: Windows disinstalla la
versione precedente prima di installare la nuova, quindi senza quella condizione ogni
aggiornamento porterebbe via token e certificato - e con un'impronta nuova ogni dashboard
remota si fermerebbe mostrando un messaggio che parla di qualcuno in mezzo alla connessione.
Un aggiornamento non deve somigliare a un attacco. **Nessuno dei due conosce alcun token**: il servizio se lo
procura da se' al primo avvio, quindi non c'e' alcun segreto da passare all'installazione, da
registrare in un log, o da lasciarsi dietro se fallisce a meta'.

Il `.deb` installa anche `man observer` e `man observer-dashboard`, ed e' verificato da
**lintian** dentro la CI: il job `pack-linux` lo esegue con `--fail-on error` sul pacchetto
appena costruito. L'unico tag sovrascritto e' `embedded-library` - `libSkiaSharp.so` porta
`freetype`, `libjpeg` e `libpng` compilati dentro, e una variante collegata alle librerie di
sistema non esiste. La ragione sta scritta in `packaging/linux/debian/lintian-overrides`,
perche' una vulnerabilita' in una di quelle tre non si chiude aggiornando Debian.

### Nota sulla globalizzazione

`InvariantGlobalization` **non** va in `Directory.Build.props`: verificato che lì spegne
in silenzio gli analyzer CA1305 e CA1310, cioè proprio quelli che impediscono il parsing
dipendente dalla cultura in `/proc`. L'invarianza a runtime è garantita dai
`runtimeconfig.template.json`, che ogni progetto **eseguibile** deve avere.

## Code signing policy

Questa e' l'unica sezione del README in inglese, e non e' una svista: e' una
dichiarazione formale, con una parte a testo obbligato, che SignPath Foundation
richiede a chi chiede la firma gratuita per un progetto open source. Tradurla la
renderebbe inutile.

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

## Licenza

[MIT](LICENSE)