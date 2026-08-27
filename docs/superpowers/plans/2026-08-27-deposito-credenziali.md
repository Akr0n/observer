# Deposito del token, rotazione e CLI — piano di implementazione

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans.

**Goal:** il servizio genera e custodisce da solo il proprio token di macchina, cosi' che un
installer non debba conoscerlo, scriverlo, o registrarlo nel proprio log.

**Perche' viene prima dell'installer.** Oggi il servizio si rifiuta di partire senza
`Observer:ApiToken`. Un installer dovrebbe quindi generare un segreto e depositarlo — cioe'
conoscerlo, tracciarlo, e lasciarselo dietro se fallisce a meta'. Dopo questo piano l'installer
copia file e registra un servizio, e basta.

## Le regole misurate

Tutto quanto segue e' stato **eseguito**, non dedotto. Sono le regole che rendono sicuro un
deposito di credenziali su Windows, e quasi nessuna e' ovvia.

### La cartella e' il perimetro, non il file

1. **Un utente standard puo' creare una sottocartella di `C:\ProgramData` e ne diventa
   proprietario.** Quel percorso concede a `Users` la creazione di sottocartelle. Questo apre
   l'attacco di **prelazione**: l'attaccante prepara la cartella prima che il servizio parta.
2. **Il PROPRIETARIO e' il controllo portante, non la DACL.** Misurato: una cartella con DACL
   perfetta — protetta, solo SYSTEM e Administrators, l'utente non nominato — ma **posseduta**
   da un utente standard e' stata riaperta da quell'utente con una sola chiamata, perche' il
   proprietario ha `WRITE_DAC` implicito. Una guardia che ispeziona le sole ACE dice "sicura" a
   una cartella che non lo e'.
3. **Il controllo del proprietario non e' aggirabile**: `SetOwner` verso SYSTEM o verso
   `BUILTIN\Administrators` da un utente non privilegiato fallisce con
   `InvalidOperationException: The security identifier is not allowed to be the owner of this
   object`.
4. **Un utente standard puo' creare una GIUNZIONE di directory senza alcun privilegio.** Non
   serve `SeCreateSymbolicLinkPrivilege` ne' la modalita' sviluppatore: `mklink /J` riesce da
   sessione non elevata. Il percorso "esiste", ma i dati finiscono dove decide l'attaccante.
   **Il controllo `FileAttributes.ReparsePoint` va fatto PER PRIMO**, prima di leggere qualunque
   ACL e prima di qualunque riparazione: altrimenti si corregge la cartella dell'attaccante e ci
   si deposita dentro il token.
5. **Chi possiede la cartella puo' cancellare il file anche quando la DACL del file lo
   esclude** (`FILE_DELETE_CHILD`). Misurato: lettura negata, cancellazione riuscita. Difendere
   il solo file non serve a niente.
6. **Ereditare non basta, ed e' gia' sbagliato senza alcun attaccante.** La DACL di
   `C:\ProgramData` concede a `BUILTIN\Users` lettura ereditabile: una sottocartella che si
   limiti a ereditare produce un file leggibile da **ogni utente della macchina**.
7. Buona notizia, e anch'essa misurata: **una volta indurita, la cartella e' fuori portata.**
   Con proprietario SYSTEM o Administrators e DACL protetta, un utente standard non riesce a
   elencarla, a scriverci, a rinominarla ne' a cancellarla.

### Le API mentono in tre punti

8. **Creare la cartella con una `DirectorySecurity` esplicita, quando la cartella esiste gia',
   riesce in silenzio e non applica niente.** Nessuna eccezione, DACL ostile intatta, file gia'
   piantati ancora li'. Un servizio che "crea la cartella con la DACL giusta e va avanti" crede
   di aver messo in sicurezza il percorso e non ha fatto nulla.
9. **`FileMode.Create` su un file gia' esistente IGNORA la `FileSecurity` passata.** Il
   security descriptor si applica solo alla creazione, mai all'apertura o al troncamento, e la
   chiamata riesce senza errore: la DACL ostile sopravvive e il token ci finisce dentro.
   L'unica forma corretta e' `File.Delete` seguito da **`FileMode.CreateNew`**.
10. **`File.Exists` restituisce `False` su un file che esiste ma e' protetto davvero.** La CLI e
    il servizio non devono ramificare su `File.Exists`: devono provare ad aprire e distinguere
    le eccezioni.

### La scrittura atomica va fatta in un modo preciso

11. `File.Move(temporaneo, destinazione, overwrite: true)` fa vincere la DACL del
    **temporaneo**, non quella del file sostituito. Misurato in due sessioni indipendenti: un
    temporaneo con DACL ereditata **declassa** il deposito a leggibile da chiunque, in silenzio.
12. Quindi il temporaneo va creato **gia' con la DACL protetta**, con
    `FileSystemAclExtensions.Create(..., FileMode.CreateNew, ..., sicurezza)`. Cosi' la `Move`
    e' atomica **e** conserva la protezione. Disponibile su `net10.0` senza pacchetti NuGet.
13. Un `File.Replace` fallito lascia il **temporaneo sul disco col segreto in chiaro** e con
    DACL ereditata. Il temporaneo va cancellato in un `finally`, sempre.

### Generazione

14. `RandomNumberGenerator.GetBytes(32)` — 256 bit — codificato **Base64Url**, che non contiene
    caratteri da codificare in un header `Authorization`.

## Le decisioni

### D1 — Cosa fa il servizio se non riesce a mettere in sicurezza il deposito

Dipende da **come sta girando**, e non e' una scappatoia: sono due situazioni diverse.

- **Come servizio di sistema** (`WindowsServiceHelpers.IsWindowsService()` oppure
  `SystemdHelpers.IsSystemdService()`): **si rifiuta di partire.** Un servizio che deposita in
  silenzio un token leggibile da tutti e' peggio di un servizio che non parte. Un servizio che
  non parte si nota subito.
- **Lanciato a mano** (`dotnet run` durante lo sviluppo, utente standard, `/etc` non
  scrivibile): **token effimero in memoria**, mai scritto su disco, **stampato all'avvio**
  perche' lo sviluppatore possa esportarlo. Il percorso di rete resta usabile per quella sola
  esecuzione; il canale locale non ne ha comunque bisogno dal piano 2.

**Mai** un ripiego per-utente su disco: sposterebbe il segreto in un posto meno protetto
facendo credere di averlo messo al sicuro.

`Observer:ApiToken` esplicito in configurazione **vince sempre** su tutto: e' la
retrocompatibilita', ed e' cio' che tiene in piedi i test e la CI.

### D2 — Il valore del token lo legge la CLI dal file, non un endpoint

Un endpoint che restituisse il token lo regalerebbe a **ogni utente interattivo** della
macchina, perche' dal piano 2 ogni utente interattivo e' un chiamante locale legittimo.
Marcarlo `SoloDaLocale` non aiuta: quel marcatore restringe la *provenienza*, e la provenienza
e' locale per tutti.

Servirebbe un `CallerKind` in piu', ottenuto da `GetNamedPipeClientProcessId` piu' l'apertura
del token del processo chiamante — cioe' riscrivere a mano un controllo che NTFS fa gia'
meglio, con in piu' una finestra TOCTOU fra la lettura del PID e l'apertura del processo. **Una
ACL non ha TOCTOU: la valuta il kernel all'apertura.**

Quindi: **il valore** viene dal file, e l'ACL e' l'autorizzazione. **Lo stato** — esiste? da
quando? la precedente quando scade? — si chiede al servizio sul canale locale, in metadati e
mai in valori.

### D3 — Tre verbi, non quattro

`show-key` duplica `share`: l'unica differenza difendibile e' il formato di uscita, che e'
un'opzione e non un verbo. Due comandi che stampano lo stesso segreto raddoppiano la superficie
da rivedere ogni volta che si cambia idea sulla consegna.

| verbo | elevazione | cosa fa |
| --- | --- | --- |
| `share` | **si** | consegna il token a un umano che deve configurare un ALTRO computer |
| `rotate-key` | **si** | genera una chiave nuova, conserva la precedente con scadenza |
| `doctor` | **no** | diagnosi: dove sta il deposito, com'e' protetto, cosa vede il client |

`doctor` e' il piu' importante dei tre, perche' e' quello che spiega gli altri due quando
falliscono. Deve emettere quattro verdetti distinti, tutti osservati:

- **PROTETTO** — proprietario SYSTEM o Administrators, nessuna ACE per altri;
- **NON PROTETTO** — `BUILTIN\Users` puo' leggerlo, il caso della DACL ereditata;
- **FINTO PROTETTO** — la DACL nomina solo SYSTEM e Administrators, **ma il proprietario e' un
  utente**, che puo' riconcedersi l'accesso quando vuole. E' il verdetto che nessuno
  scriverebbe senza averlo misurato;
- **SCONOSCIUTO** — da qui non si riesce nemmeno a elencare la cartella.

E accanto la riga che disinnesca il panico: *per guardare questa macchina non serve alcun
token; il token serve solo perche' un ALTRO computer possa interrogarla.*

### D4 — Consegna del segreto

`share` senza opzioni stampa il token con il contesto attorno, perche' un umano deve copiarlo.
Con `--stdout` stampa **solo** il token e **senza newline finale**: catturando l'uscita in una
variabile di shell, un ritorno a capo finale entrerebbe nel valore e il confronto a tempo
costante lo rifiuterebbe byte a byte.

La cronologia di PowerShell registra la **riga digitata**, non l'output: nessuno dei tre verbi
prende il segreto come argomento, quindi non finisce in cronologia. Non aggiungere mai un verbo
che lo faccia.

## Task

### Task 1: il modello e la generazione
- Create: `src/Observer.Service/Credentials/MachineCredentials.cs`, `TokenGenerator.cs`
- Test: `tests/Observer.Service.Tests/MachineCredentialsTests.cs`

`MachineCredentials(string Current, string? Previous, DateTimeOffset? PreviousExpiresAt)` con
`Accetta(string presentato, DateTimeOffset adesso)`. La rotazione conserva la precedente per 24
ore: senza, ruotare taglierebbe fuori ogni client remoto all'istante.

### Task 2: la fiducia nella cartella
- Create: `src/Observer.Service/Credentials/DirectoryTrust.cs` (cross-platform) e
  `WindowsDirectoryTrust.cs` (`[SupportedOSPlatform("windows")]`)
- Test: `tests/Observer.Service.Tests/DirectoryTrustTests.cs`

Ordine dei controlli, **vincolato**: reparse point, poi proprietario, poi DACL. La riparazione
prende **prima la proprieta'**, poi scrive la DACL: al contrario, l'attaccante la disfa subito.

### Task 3: il deposito
- Create: `src/Observer.Service/Credentials/CredentialStore.cs`
- Test: `tests/Observer.Service.Tests/CredentialStoreTests.cs`

Ricetta di scrittura: temporaneo nella stessa cartella creato **gia' protetto** con
`FileMode.CreateNew`, scrittura, chiusura, `File.Move(overwrite: true)`, cancellazione del
temporaneo in `finally`.

### Task 4: il servizio si autoprovvede
- Modify: `src/Observer.Service/Program.cs`,
  `src/Observer.Service/LocalChannel/AccessMiddleware.cs`

Precedenza: configurazione esplicita, poi deposito, poi generazione. Il confronto del token
accetta la corrente **e** la precedente non scaduta.

### Task 5: la CLI
- Create: `src/Observer.Cli/` (`AssemblyName` = `observer`), registrato in `Observer.slnx`,
  con il suo `runtimeconfig.template.json`.

Nessuna dipendenza NuGet: tre verbi non giustificano un parser, e un pacchetto in beta sotto
`TreatWarningsAsErrors` e' un rischio gratuito.

## Cosa questo piano NON puo' verificare qui

- **Il ramo elevato.** Questa macchina non ha una sessione amministrativa: il deposito creato
  DAVVERO da SYSTEM non e' osservabile da qui. La verifica passa dal servizio installato.
- **Il ramo Linux.** `.NET` in WSL non parte su questa macchina; la verifica passa da
  `build (ubuntu-latest)`.
