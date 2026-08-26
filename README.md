# Observer

![build](https://github.com/Akr0n/observer/actions/workflows/build.yml/badge.svg)

Dashboard cross-platform per il monitoraggio dei parametri vitali della macchina
e dei dispositivi presenti sulla rete locale. Gira su Windows e Linux.

> **Stato:** sviluppo iniziale, non ancora utilizzabile.

## Architettura

Il progetto è diviso in un servizio headless e un client desktop: su Windows i
servizi girano in Session 0 e non possono mostrare un'interfaccia grafica, quindi
raccolta e visualizzazione devono essere due processi distinti.

| Progetto | Ruolo |
| --- | --- |
| `src/Observer.Core` | Modelli condivisi e astrazioni per la raccolta delle metriche |
| `src/Observer.Service` | Servizio headless (Windows Service / systemd): campiona, persiste su SQLite, espone i dati via HTTP |
| `src/Observer.App` | Client desktop Avalonia: si collega al servizio e visualizza |
| `tests/Observer.Core.Tests` | Test unitari su `Observer.Core` |

Il client può puntare al servizio in esecuzione sulla stessa macchina o su un'altra.

## Requisiti

- .NET SDK 10.0
- Windows 10/11 oppure una distribuzione Linux con ambiente grafico

## Sviluppo

```bash
dotnet build
dotnet test
```

Avvio di servizio e client, in due terminali separati:

```bash
dotnet run --project src/Observer.Service
dotnet run --project src/Observer.App
```

## Licenza

[MIT](LICENSE)