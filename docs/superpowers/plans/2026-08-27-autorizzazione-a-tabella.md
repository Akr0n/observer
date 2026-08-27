# Autorizzazione a tabella — piano di implementazione

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans.

**Goal:** sostituire il middleware che pretende il bearer token su ogni richiesta con una
**funzione pura** che decide in base a chi chiama, a cosa chiede e a quale credenziale presenta.

**Architecture:** la decisione e' una funzione senza stato ne' I/O, verificabile con una tabella
esaustiva su entrambi i runner. Il middleware si limita a raccogliere i tre ingressi e ad
applicare l'esito.

**Tech Stack:** .NET 10, minimal API, la classificazione del chiamante gia' prodotta dal piano 1.

## Cosa cambia davvero

**Alla fine di questo piano un chiamante locale identificato NON deve piu' presentare il token.**
E' il primo cambiamento di comportamento visibile dell'intero progetto, ed e' il motivo per cui
il piano 1 lo aveva deliberatamente rimandato.

Sulle connessioni di rete non cambia nulla: token obbligatorio, come oggi.

## La tabella

Tre ingressi, dodici casi, nessuna scorciatoia.

| chi chiama | portata dell'endpoint | token valido | esito |
| --- | --- | --- | --- |
| `LocaleIdentificato` | `Ovunque` | si | **Consentito** |
| `LocaleIdentificato` | `Ovunque` | no | **Consentito** |
| `LocaleIdentificato` | `SoloLocale` | si | **Consentito** |
| `LocaleIdentificato` | `SoloLocale` | no | **Consentito** |
| `ArrivatoDallaRete` | `Ovunque` | si | **Consentito** |
| `ArrivatoDallaRete` | `Ovunque` | no | Rifiutato (401) |
| `ArrivatoDallaRete` | `SoloLocale` | si | **NonEsiste (404)** |
| `ArrivatoDallaRete` | `SoloLocale` | no | **NonEsiste (404)** |
| `NonIdentificabile` | `Ovunque` | si | Rifiutato (401) |
| `NonIdentificabile` | `Ovunque` | no | Rifiutato (401) |
| `NonIdentificabile` | `SoloLocale` | si | **NonEsiste (404)** |
| `NonIdentificabile` | `SoloLocale` | no | **NonEsiste (404)** |

### Le quattro decisioni che la tabella incorpora

1. **Il locale identificato passa senza token, su tutto.** E' l'obiettivo del progetto: sulla
   macchina il sistema operativo sa gia' chi chiama, e un segreto condiviso e' lo strumento
   sbagliato.

2. **`NonIdentificabile` viene rifiutato ANCHE con un token valido.** Non e' pignoleria: il
   livello di impersonation lo sceglie il client, e con `Anonymous` un chiamante si rende
   unilateralmente non identificabile pur restando in grado di presentare un token. Chi ha il
   token puo' usare il canale di rete; sul canale locale la regola e' *l'identita' non
   determinabile rifiuta*, senza eccezioni che la svuotino.

3. **`SoloLocale` risponde 404 e non 403 a chi non e' locale.** Gli endpoint di appaiamento del
   piano 3 ruotano le chiavi: chi ruba il token non deve poter chiudere fuori il proprietario, e
   un 404 non conferma nemmeno che l'endpoint esista.

4. **Chi decide QUALI utenti locali sono ammessi non e' questa funzione.** Su Windows lo decide
   la DACL della pipe, che rifiuta alla connect; su Linux il modo del file del socket. La
   funzione verifica due cose soltanto: che il chiamante sia davvero locale e che sia
   identificabile. Metterci dentro una lista di SID ammessi duplicherebbe una decisione che il
   sistema operativo prende meglio, e che il piano 1 gli ha gia' affidato.

### Perche' i valori zero sono quelli che negano

`AccessDecision.Rifiutato = 0`, `EndpointScope.SoloLocale = 0`, e `CallerKind.NonIdentificabile`
vale gia' zero dal piano 1. Un campo dimenticato, una struct non inizializzata o un ramo aggiunto
per distrazione **negano**.

**Limite da non nascondere.** La protezione vale per la FUNZIONE, non per il modo in cui gli
endpoint dichiarano la portata. La restrizione e' a opt-in: un endpoint senza marcatore vale
`Ovunque`, cioe' resta com'e' oggi. Chi aggiungesse un endpoint solo-locale scordandosi il
marcatore lo esporrebbe alla rete dietro il token, non lo renderebbe irraggiungibile. Il rimedio
non e' una furbizia nel codice ma il fatto che gli endpoint di appaiamento del piano 3 nasceranno
insieme, in un unico gruppo di rotte marcato una volta sola.

## Task

### Task 1: la funzione pura e la sua tabella

- Create: `src/Observer.Service/LocalChannel/AccessPolicy.cs`
- Test: `tests/Observer.Service.Tests/AccessPolicyTests.cs`

Dodici casi espliciti, piu' una prova che i valori zero degli enum siano quelli restrittivi.

### Task 2: il middleware e la portata degli endpoint

- Modify: `src/Observer.Service/Program.cs`
- Create: `src/Observer.Service/LocalChannel/EndpointScopeExtensions.cs`

Il middleware va spostato **dopo `UseRouting()`**, altrimenti `context.GetEndpoint()` e' null e
la portata non e' leggibile. `UseRouting()` va chiamato esplicitamente per fissare quella
posizione.

### Task 3: la prova end-to-end sui due canali

- Test: aggiunte a `CanaleLocaleWindowsTests` e `CanaleLocaleLinuxTests`

Un endpoint marcato `SoloDaLocale` deve rispondere **404 sul TCP** e **200 sulla pipe o sul
socket**, senza alcun token. E un endpoint normale deve rispondere **200 sul canale locale senza
token** e **401 sul TCP senza token**.

## Verifica finale, che nessun test puo' fare

Sul servizio vero, dopo l'installazione come LocalSystem:

- pipe **senza** token: **200** — cambiato rispetto al piano 1, ed e' l'obiettivo;
- TCP **senza** token: **401** — invariato;
- TCP **con** token: **200** — invariato.