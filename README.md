# Observer

![build](https://github.com/Akr0n/observer/actions/workflows/build.yml/badge.svg)

Dashboard cross-platform per il monitoraggio dei parametri vitali della macchina
e dei dispositivi presenti sulla rete locale. Gira su Windows e Linux.

> **Stato:** in sviluppo. Il servizio campiona CPU e RAM e le espone via HTTP
> autenticato, su Windows e su Linux. Mancano ancora la persistenza, il flusso in
> tempo reale e il client desktop, quindi non c'è ancora una dashboard da guardare.

## Architettura

Il progetto è diviso in un servizio headless e un client desktop: su Windows i
servizi girano in Session 0 e non possono mostrare un'interfaccia grafica, quindi
raccolta e visualizzazione devono essere due processi distinti.

| Progetto | Ruolo |
| --- | --- |
| `src/Observer.Core` | Modelli condivisi, astrazione dei collector e adattatori di piattaforma |
| `src/Observer.Service` | Servizio headless (Windows Service / systemd): campiona a 1 Hz ed espone i dati via HTTP autenticato. La persistenza su SQLite è prevista, non ancora presente |
| `src/Observer.App` | Client desktop Avalonia: si collegherà al servizio e visualizzerà. Ancora allo scaffolding |
| `tests/Observer.Core.Tests` | Test unitari su `Observer.Core` |

Il client può puntare al servizio in esecuzione sulla stessa macchina o su un'altra.

### Aggiungere una metrica

L'unico punto di estensione è `IMetricCollector`. Ogni collector pubblica i propri
`MetricDescriptor` e restituisce una lista di `MetricPoint`, in un formato uguale per
tutte le sorgenti. La dimensione per istanza — il core, il disco, l'interfaccia di rete —
è un campo stringa del punto, non una gerarchia di tipi: per questo per-disco e
per-processo passano dalla stessa interfaccia senza modificarla. Le unità di misura sono
un tipo aperto, quindi un sensore in `rpm` o in `V` non richiede di toccare il Core.

In pratica: si scrive una classe nuova e la si registra in
`src/Observer.Core/Composition/ObserverMetrics.cs`. Nessun altro file va modificato.

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

Tutti richiedono il bearer token.

| Endpoint | Cosa restituisce |
| --- | --- |
| `GET /metrics/catalog` | le metriche esistenti, con nome leggibile e unità |
| `GET /metrics/latest` | l'ultimo campionamento |
| `GET /metrics/series` | quali serie sono state davvero misurate su questa macchina |
| `GET /metrics/history` | i punti storici; `resolution` accetta `auto`, `raw`, `1m`, `5m` |
| `GET /metrics/storage` | dove scrive, quanto occupa, fin dove ha aggregato |

`auto` sceglie la risoluzione più fine ancora disponibile per l'intervallo richiesto: il
grezzo di ieri è stato cancellato, e restituire un grafico vuoto si leggerebbe come
"macchina non monitorata".

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

Il servizio ascolta su `0.0.0.0:5057` ed espone la telemetria della macchina, quindi
**ogni endpoint richiede un bearer token** e senza token configurato il servizio si
rifiuta di partire. Il token non va committato: si mette in
`src/Observer.Service/appsettings.Local.json` (già escluso da `.gitignore`)

```json
{ "Observer": { "ApiToken": "un-valore-lungo-e-casuale" } }
```

oppure nella variabile d'ambiente `Observer__ApiToken`.

Avvio di servizio e client, in due terminali separati:

```bash
dotnet run --project src/Observer.Service
```

```bash
dotnet run --project src/Observer.App
```

Lettura delle metriche:

```bash
curl -H "Authorization: Bearer $Observer__ApiToken" http://localhost:5057/metrics/latest
```

### Nota sulla globalizzazione

`InvariantGlobalization` **non** va in `Directory.Build.props`: verificato che lì spegne
in silenzio gli analyzer CA1305 e CA1310, cioè proprio quelli che impediscono il parsing
dipendente dalla cultura in `/proc`. L'invarianza a runtime è garantita dai
`runtimeconfig.template.json`, che ogni progetto **eseguibile** deve avere.

## Licenza

[MIT](LICENSE)