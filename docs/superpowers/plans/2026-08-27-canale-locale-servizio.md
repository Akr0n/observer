# Canale locale nel servizio — piano di implementazione

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** dare a `Observer.Service` un secondo ascolto locale — named pipe su Windows, socket
unix su Linux — che serve gli stessi endpoint del TCP, e sapere con certezza se il chiamante e'
davvero locale e chi e'.

**Architecture:** un solo `WebApplication` e un solo Kestrel con due endpoint. Il cablaggio non
puo' stare nei top-level statements di `Program.cs` perche' `[SupportedOSPlatform]` non li
copre, quindi vive in `src/Observer.Service/LocalChannel/`, con una classe per piattaforma
marcata `[SupportedOSPlatform]` e un punto d'ingresso cross-platform che le chiama dentro
`if (OperatingSystem.IsWindows())` / `IsLinux()`.

**Tech Stack:** .NET 10, Kestrel (`ListenNamedPipe` / `ListenUnixSocket`),
`IConnectionNamedPipeFeature`, `IConnectionSocketFeature`, `PipeSecurity`,
`GetNamedPipeClientComputerName` via `[LibraryImport]`, `SO_PEERCRED` via
`Socket.GetRawSocketOption`, xunit 2.9.3.

## Questo piano NON cambia l'autorizzazione

Da rileggere prima di ogni task, perche' e' la tentazione ricorrente. Alla fine di questo piano
**il bearer token resta obbligatorio su tutti i canali, canale locale compreso**. La
classificazione del chiamante viene calcolata, esposta e verificata, ma non concede nulla.

Il motivo: la riga `if (pipe != null) salta il token` e' esattamente il difetto che la specifica
documenta, e un piano che apre il canale *e* cambia l'autorizzazione nello stesso passo non ha
un punto in cui si possa dire "il canale funziona, l'autorizzazione e' ancora quella di prima".
Il cambio di autorizzazione e' il piano 2, e parte da una classificazione gia' provata.

Conseguenza pratica: **al termine di questo piano il comportamento visibile del servizio non
cambia**. Un client che arriva dalla pipe con il token giusto riceve i dati; senza token riceve
401, identico a oggi.

## Global Constraints

Valgono per ogni task, senza ripeterli.

- **`TreatWarningsAsErrors=true`**, `EnforceCodeStyleInBuild=true`,
  `AnalysisLevel=latest-recommended`, `Nullable=enable` (`Directory.Build.props`). Un warning
  analyzer **fa fallire la build**, su entrambi i runner.
- **CA1416**: ogni classe (anche annidata) che tocca `PipeSecurity`, `PipeAccessRule`,
  `SecurityIdentifier`, `WellKnownSidType`, `WindowsIdentity`, `RunAsClient` o `UseNamedPipes`
  porta il proprio `[SupportedOSPlatform("windows")]`; le API unix-only portano
  `[SupportedOSPlatform("linux")]`. **Ogni sito di chiamata da codice cross-platform sta dentro
  `if (OperatingSystem.IsWindows())` o `if (OperatingSystem.IsLinux())` scritto per esteso**:
  l'attributo su una local function non viene onorato, non copre il corpo di una lambda, e una
  guardia estratta in una proprieta' di comodo non viene seguita dall'analyzer.
- **`dotnet_style_readonly_field`** e **`csharp_prefer_static_local_function`** hanno severita'
  `warning` e quindi rompono la build.
- **`InvariantGlobalization` non va rimesso** in `Directory.Build.props`: spegne CA1305/CA1310.
  Usare sempre `CultureInfo.InvariantCulture` esplicito.
- **P/Invoke con `[LibraryImport]`**, mai `[DllImport]`: e' la forma gia' usata in
  `Observer.Core`.
- `.editorconfig` impone `insert_final_newline = false` per i `.cs`.
- Commenti e messaggi di commit in **italiano**; testo visibile all'utente in **inglese**.
- **Non rinominare** il job `build` ne' i valori della matrice `os` in
  `.github/workflows/build.yml`.

## File Structure

**Nuovi, in `src/Observer.Service/LocalChannel/`:**

| File | Responsabilita' |
| --- | --- |
| `EndpointUrl.cs` | funzione **pura** che dice se un URL di endpoint Kestrel e' utilizzabile. Cross-platform, nessuna I/O. |
| `LocalChannelOptions.cs` | nome della pipe e percorso del socket, da configurazione, con default. Cross-platform. |
| `CallerOrigin.cs` | il risultato della classificazione: enum + record. Cross-platform. |
| `WindowsNamedPipe.cs` | `[SupportedOSPlatform("windows")]` — DACL e ascolto sulla pipe. |
| `WindowsCallerIdentity.cs` | `[SupportedOSPlatform("windows")]` — `GetNamedPipeClientComputerName` e lettura del token. |
| `LinuxUnixSocket.cs` | `[SupportedOSPlatform("linux")]` — percorso, bonifica, modo del file. |
| `LinuxCallerIdentity.cs` | `[SupportedOSPlatform("linux")]` — `SO_PEERCRED`. |
| `LocalChannel.cs` | punto d'ingresso cross-platform chiamato da `Program.cs`. |

**Modificati:** `src/Observer.Service/Program.cs` (due blocchi),
`src/Observer.Service/appsettings.json` (una sezione).

**Nuovi test, in `tests/Observer.Service.Tests/`:** `SoloSu.cs` (attributi di salto),
`EndpointUrlTests.cs`, `BancoKestrelReale.cs`, `BancoKestrelRealeTests.cs`,
`CanaleLocaleWindowsTests.cs`, `CanaleLocaleLinuxTests.cs`.

---

### Task 1: convalida degli URL degli endpoint

Prima di tutto il resto, perche' e' la rete di sicurezza che rende innocui gli errori dei task
successivi. **Misurato: `http://unix:C:\percorso\x.sock` non produce ne' eccezione ne' warning —
Kestrel lega `[::]:80` su tutte le interfacce.** Si crede di aver aperto un canale privato e si
e' aperta la LAN, con la telemetria dietro. Nessuno dei 201 test attuali puo' accorgersene,
perche' `WebApplicationFactory` sostituisce Kestrel con un `TestServer` e la sezione `Kestrel`
non viene mai analizzata.

**Files:**
- Create: `src/Observer.Service/LocalChannel/EndpointUrl.cs`
- Modify: `src/Observer.Service/Program.cs` (dopo `storage.Validate();`)
- Test: `tests/Observer.Service.Tests/EndpointUrlTests.cs`

**Interfaces:**
- Consumes: niente.
- Produces: `internal static class EndpointUrl` con
  `public const int MaxUnixSocketPathBytes = 107;` e
  `public static string? Problema(string url)` — `null` se l'URL va bene, altrimenti la frase in
  inglese da mostrare.

- [ ] **Step 1: scrivi il test che fallisce**

```csharp
using System.Text;
using Observer.Service.LocalChannel;

namespace Observer.Service.Tests;

/// <summary>
/// Un URL di endpoint scritto male non fallisce: fallisce PEGGIO.
/// </summary>
/// <remarks>
/// Misurato: con "http://unix:C:\percorso\x.sock" Kestrel non lancia e non avvisa, lega
/// [::]:80 su TUTTE le interfacce e ci mette dietro la telemetria della macchina. Questa
/// funzione esiste per trasformare quel silenzio in un rifiuto all'avvio.
/// </remarks>
public class EndpointUrlTests
{
    [Theory]
    [InlineData("http://0.0.0.0:5057")]
    [InlineData("https://0.0.0.0:7051")]
    [InlineData("http://unix:/run/observer/observer.sock")]
    [InlineData("http://pipe:/Observer")]
    public void UrlValidi_NonProduconoAlcunProblema(string url) =>
        Assert.Null(EndpointUrl.Problema(url));

    [Theory]
    // Il caso che ha aperto la porta 80 su tutte le interfacce senza dire niente.
    [InlineData("http://unix:C:\\Users\\tizio\\AppData\\Local\\Temp\\x.sock")]
    // Percorso unix relativo: Kestrel lo rifiuta a StartAsync, cioe' troppo tardi per capirlo.
    [InlineData("http://unix:relativo.sock")]
    // Pipe senza la barra: stessa trappola del percorso Windows.
    [InlineData("http://pipe:Observer")]
    [InlineData("http://pipe:/")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("non-un-url")]
    public void UrlRotti_SpieganoIlProblema(string url) =>
        Assert.False(string.IsNullOrWhiteSpace(EndpointUrl.Problema(url)));

    [Fact]
    public void PercorsoDelSocketDi107Byte_Accettato_Di108_No()
    {
        // Il messaggio d'errore di .NET dice "between 1 and 108 characters" e MENTE: non conta
        // il terminatore NUL. Misurato per bisezione: 107 passa, 108 lancia
        // ArgumentOutOfRangeException. Una guardia scritta a 108 lascia passare esattamente il
        // caso di confine, che e' l'unico che conta.
        string a107 = "/" + new string('a', 106);
        string a108 = "/" + new string('a', 107);

        Assert.Equal(107, Encoding.UTF8.GetByteCount(a107));
        Assert.Equal(108, Encoding.UTF8.GetByteCount(a108));

        Assert.Null(EndpointUrl.Problema("http://unix:" + a107));
        Assert.NotNull(EndpointUrl.Problema("http://unix:" + a108));
    }

    [Fact]
    public void IlConteggioEInByteNonInCaratteri()
    {
        // Un percorso di 81 caratteri, meta' accentati, supera i 107 byte in UTF-8. Contare i
        // caratteri farebbe passare un percorso che il sistema operativo rifiuta.
        string accentato = "/" + new string('e', 40) + new string('\u00e8', 40);

        Assert.True(accentato.Length <= 107);
        Assert.True(Encoding.UTF8.GetByteCount(accentato) > 107);
        Assert.NotNull(EndpointUrl.Problema("http://unix:" + accentato));
    }
}
```

- [ ] **Step 2: guarda il test fallire**

Esegui: `dotnet test tests/Observer.Service.Tests --filter "FullyQualifiedName~EndpointUrlTests"`
Atteso: **errore di compilazione** — `EndpointUrl` non esiste. E' il fallimento giusto.

- [ ] **Step 3: scrivi l'implementazione minima**

```csharp
using System.Globalization;
using System.Text;

namespace Observer.Service.LocalChannel;

/// <summary>
/// Dice se un URL di endpoint di Kestrel e' utilizzabile, prima che Kestrel ci provi.
/// </summary>
/// <remarks>
/// Funzione PURA: nessuna I/O, nessun ambiente, quindi verificabile con una tabella su
/// entrambi i runner invece che avviando un host.
/// <para>
/// Esiste perche' i modi di sbagliare non sono equivalenti. Un percorso di socket relativo fa
/// fallire l'avvio, ed e' il caso buono. Un percorso in stile Windows dentro "http://unix:"
/// non fallisce affatto: Kestrel lega [::]:80 su TUTTE le interfacce, senza eccezione e senza
/// warning, e ci mette dietro la telemetria della macchina.
/// </para>
/// </remarks>
internal static class EndpointUrl
{
    /// <summary>Byte utili nel percorso di un socket unix. <b>107, non 108.</b></summary>
    /// <remarks>
    /// La struct sockaddr_un ha 108 byte di sun_path, ma uno serve al terminatore. Il
    /// messaggio di .NET dice "must be between 1 and 108 characters, inclusive" ed e' falso su
    /// due punti: il limite vero e' 107, e il conteggio e' in BYTE UTF-8, non in caratteri.
    /// Verificato per bisezione: 107 accettato, 108 rifiutato.
    /// </remarks>
    public const int MaxUnixSocketPathBytes = 107;

    private const string PrefissoUnix = "unix:";
    private const string PrefissoPipe = "pipe:";

    /// <summary>Il problema dell'URL, in inglese, oppure null se non ce ne sono.</summary>
    /// <param name="url">L'URL cosi' come sta in configurazione.</param>
    /// <returns>La frase da mostrare, oppure null.</returns>
    public static string? Problema(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "An empty endpoint URL was configured. Remove the entry or give it a value.";
        }

        int separatore = url.IndexOf("://", StringComparison.Ordinal);

        if (separatore <= 0)
        {
            return Rotto(url, "it has no scheme, so it isn't a URL at all");
        }

        string resto = url[(separatore + 3)..];

        if (resto.StartsWith(PrefissoUnix, StringComparison.OrdinalIgnoreCase))
        {
            return ProblemaUnix(url, resto[PrefissoUnix.Length..]);
        }

        if (resto.StartsWith(PrefissoPipe, StringComparison.OrdinalIgnoreCase))
        {
            return ProblemaPipe(url, resto[PrefissoPipe.Length..]);
        }

        return Uri.TryCreate(url, UriKind.Absolute, out _)
            ? null
            : Rotto(url, "it isn't a well-formed absolute URL");
    }

    private static string? ProblemaUnix(string url, string percorso)
    {
        if (!percorso.StartsWith('/'))
        {
            // Il caso pericoloso: qui finisce anche "C:\...". Senza questo controllo Kestrel
            // non protesta e apre la porta 80 su tutte le interfacce.
            return Rotto(
                url,
                "the unix socket path must be absolute and start with '/'. A Windows-style " +
                "path here does NOT fail: Kestrel silently listens on port 80 on every " +
                "network interface instead");
        }

        int byteDelPercorso = Encoding.UTF8.GetByteCount(percorso);

        return byteDelPercorso > MaxUnixSocketPathBytes
            ? Rotto(
                url,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"the unix socket path is {byteDelPercorso} bytes long and the limit is " +
                    $"{MaxUnixSocketPathBytes}. The limit counts UTF-8 bytes, not characters"))
            : null;
    }

    private static string? ProblemaPipe(string url, string nome)
    {
        if (!nome.StartsWith('/'))
        {
            return Rotto(url, "a named pipe endpoint must be written as http://pipe:/<name>");
        }

        return nome.Length > 1
            ? null
            : Rotto(url, "the pipe name is missing after http://pipe:/");
    }

    private static string Rotto(string url, string motivo) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"The endpoint URL \"{url}\" can't be used: {motivo}.");
}
```

- [ ] **Step 4: guarda il test passare**

Esegui: `dotnet test tests/Observer.Service.Tests --filter "FullyQualifiedName~EndpointUrlTests"`
Atteso: tutti verdi.

- [ ] **Step 5: collega la convalida a `Program.cs`**

Subito dopo il blocco `storage.Validate();`, cioe' **prima** di `builder.Build()`. Aggiungi
`using Observer.Service.LocalChannel;` in cima al file.

```csharp
// Gli URL degli endpoint si convalidano QUI, per lo stesso motivo per cui si convalida la
// ritenzione: non tutti i modi di sbagliare falliscono. Un percorso di socket in stile
// Windows dentro "http://unix:" non fa lanciare niente e fa ascoltare Kestrel sulla porta 80
// di OGNI interfaccia, con la telemetria dietro. Meglio non partire.
foreach (IConfigurationSection endpoint in
    builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren())
{
    if (endpoint["Url"] is { } url && EndpointUrl.Problema(url) is { } problema)
    {
        throw new InvalidOperationException(
            $"Kestrel endpoint \"{endpoint.Key}\" is misconfigured. {problema}");
    }
}
```

- [ ] **Step 6: verifica che la suite intera resti verde e committa**

```bash
dotnet build -c Release && dotnet test --no-build -c Release
```

```bash
git add src/Observer.Service tests/Observer.Service.Tests && git commit -m "feat(service): rifiuta all'avvio un URL di endpoint inutilizzabile"
```

---

### Task 2: banco di prova con Kestrel vero

**Misurato: `WebApplicationFactory` sostituisce Kestrel con un `TestServer` in memoria.** Nessuno
dei 201 test attuali tocca un trasporto reale, quindi nessuno puo' verificare una pipe o un
socket. Questo task costruisce il banco che i task 3-6 useranno. Da solo non aggiunge
funzionalita', ma senza di esso i task successivi non hanno modo di dimostrare niente.

**Files:**
- Create: `tests/Observer.Service.Tests/SoloSu.cs`
- Create: `tests/Observer.Service.Tests/BancoKestrelReale.cs`
- Test: `tests/Observer.Service.Tests/BancoKestrelRealeTests.cs`

**Interfaces:**
- Consumes: `EndpointUrl` dal task 1; `AmbienteDelProcesso.Nome`, gia' esistente.
- Produces:
  - `public sealed class SoloSuWindowsAttribute : FactAttribute`
  - `public sealed class SoloSuLinuxAttribute : FactAttribute`
  - `public sealed class BancoKestrelReale : IAsyncDisposable` con
    `public static Task<BancoKestrelReale> AvviaAsync(Action<KestrelServerOptions> ascolti, Action<WebApplication>? mappa = null)`,
    `public static HttpClient ClientSu(HttpMessageHandler handler)`,
    `public IReadOnlyList<string> Indirizzi { get; }`

- [ ] **Step 1: scrivi gli attributi di salto**

xunit 2.9.3 non ha `Assert.Skip`. La forma che funziona e' un `FactAttribute` che si auto-salta.

```csharp
namespace Observer.Service.Tests;

/// <summary>Un fatto che fuori da Windows viene saltato invece che fallire.</summary>
/// <remarks>
/// xunit 2.9.3 non ha Assert.Skip: l'unico modo di saltare per piattaforma e' valorizzare
/// Skip nel costruttore dell'attributo. Saltare e' l'esito giusto — un test di named pipe che
/// fallisse su ubuntu-latest renderebbe rosso il runner sbagliato e nasconderebbe i guasti veri.
/// </remarks>
public sealed class SoloSuWindowsAttribute : FactAttribute
{
    public SoloSuWindowsAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Named pipe e identita' di Windows: eseguito solo su windows-latest.";
        }
    }
}

/// <summary>Un fatto che fuori da Linux viene saltato invece che fallire.</summary>
public sealed class SoloSuLinuxAttribute : FactAttribute
{
    public SoloSuLinuxAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "SO_PEERCRED esiste solo su Linux: eseguito solo su ubuntu-latest.";
        }
    }
}
```

- [ ] **Step 2: scrivi il banco**

Tre dettagli sono obbligatori e non ovvi, tutti commentati nel codice: `Sources.Clear()`,
l'appartenenza alla collezione `AmbienteDelProcesso`, e l'host fittizio nel `BaseAddress`.

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Observer.Service.Tests;

/// <summary>
/// Un host Kestrel VERO, con trasporti veri, avviato dentro il test.
/// </summary>
/// <remarks>
/// Serve perche' WebApplicationFactory sostituisce Kestrel con un TestServer in memoria:
/// verificato, e significa che nessuno dei test esistenti esercita un trasporto. Una named
/// pipe o un socket unix non esistono affatto sotto TestServer, e nemmeno la sezione Kestrel
/// di appsettings.json viene analizzata — motivo per cui un URL di endpoint sbagliato passa
/// la CI verde.
/// </remarks>
public sealed class BancoKestrelReale : IAsyncDisposable
{
    private readonly WebApplication app;

    private BancoKestrelReale(WebApplication app, IReadOnlyList<string> indirizzi)
    {
        this.app = app;
        Indirizzi = indirizzi;
    }

    /// <summary>Gli indirizzi su cui l'host sta davvero ascoltando.</summary>
    public IReadOnlyList<string> Indirizzi { get; }

    /// <summary>Avvia l'host con gli ascolti indicati e un endpoint di prova.</summary>
    /// <param name="ascolti">Gli endpoint da aprire.</param>
    /// <param name="mappa">Endpoint aggiuntivi, per i test che ne hanno bisogno.</param>
    /// <returns>Il banco gia' avviato.</returns>
    public static async Task<BancoKestrelReale> AvviaAsync(
        Action<KestrelServerOptions> ascolti,
        Action<WebApplication>? mappa = null)
    {
        ArgumentNullException.ThrowIfNull(ascolti);

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        // Obbligatorio: l'output dei test contiene appsettings.json, appsettings.Development.json
        // e appsettings.Local.json COPIATI da Observer.Service. Senza questa riga il banco
        // eredita la porta 5057 del servizio vero e il token di sviluppo, e i test si
        // scontrerebbero con l'istanza installata sulla macchina di chi li esegue.
        builder.Configuration.Sources.Clear();

        builder.WebHost.ConfigureKestrel(ascolti);
        builder.Logging.ClearProviders();

        WebApplication app = builder.Build();

        app.MapGet("/ping", () => "pong");
        mappa?.Invoke(app);

        await app.StartAsync().ConfigureAwait(false);

        IReadOnlyList<string> indirizzi =
            app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()?.Addresses.ToList() ?? [];

        return new BancoKestrelReale(app, indirizzi);
    }

    /// <summary>Un client che parla con questo host attraverso l'handler indicato.</summary>
    /// <param name="handler">L'handler, tipicamente con un ConnectCallback.</param>
    /// <returns>Il client.</returns>
    public static HttpClient ClientSu(HttpMessageHandler handler) =>
        // L'host nell'URI e' arbitrario: misurato, finisce solo nell'header Host e il DNS non
        // viene interpellato. Un nome sotto .invalid rende esplicito che non deve risolversi.
        new(handler, disposeHandler: true)
        {
            BaseAddress = new Uri("http://canale-locale.invalid/"),
            Timeout = TimeSpan.FromSeconds(10),
        };

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await app.StopAsync().ConfigureAwait(false);
        await app.DisposeAsync().ConfigureAwait(false);
    }
}
```

- [ ] **Step 3: scrivi il test di fumo e guardalo fallire**

```csharp
using System.Net;

namespace Observer.Service.Tests;

/// <summary>Il banco stesso funziona: senza questo, i fallimenti dei task successivi sono ambigui.</summary>
[Collection(AmbienteDelProcesso.Nome)]
public class BancoKestrelRealeTests
{
    [Fact]
    public async Task IlBancoAvviaUnKestrelVeroSuUnaPortaEffimera()
    {
        await using BancoKestrelReale banco = await BancoKestrelReale.AvviaAsync(
            opzioni => opzioni.Listen(IPAddress.Loopback, 0));

        string indirizzo = Assert.Single(banco.Indirizzi);

        using HttpClient client = new() { BaseAddress = new Uri(indirizzo) };

        Assert.Equal("pong", await client.GetStringAsync("ping", CancellationToken.None));
    }

    [Fact]
    public async Task IlBancoNonEreditaLaConfigurazioneDelServizioVero()
    {
        // Senza Sources.Clear() il banco leggerebbe l'appsettings.json copiato nell'output dei
        // test e proverebbe a legare la 5057, scontrandosi con il servizio installato.
        await using BancoKestrelReale banco = await BancoKestrelReale.AvviaAsync(
            opzioni => opzioni.Listen(IPAddress.Loopback, 0));

        Assert.DoesNotContain(
            banco.Indirizzi,
            indirizzo => indirizzo.Contains("5057", StringComparison.Ordinal));
    }
}
```

Esegui: `dotnet test tests/Observer.Service.Tests --filter "FullyQualifiedName~BancoKestrelRealeTests"`
Atteso: **errore di compilazione** finche' `BancoKestrelReale` non esiste; poi verde.

- [ ] **Step 4: verifica e committa**

```bash
dotnet build -c Release && dotnet test --no-build -c Release
```

```bash
git add tests/Observer.Service.Tests && git commit -m "test(service): banco di prova con un Kestrel vero, non il TestServer in memoria"
```

---

### Task 3: named pipe su Windows, con la sua DACL

**Files:**
- Create: `src/Observer.Service/LocalChannel/WindowsNamedPipe.cs`
- Test: `tests/Observer.Service.Tests/CanaleLocaleWindowsTests.cs`

**Interfaces:**
- Consumes: `BancoKestrelReale`, `SoloSuWindowsAttribute` dal task 2.
- Produces: `[SupportedOSPlatform("windows")] internal static class WindowsNamedPipe` con
  `public static void ConfiguraTrasporto(NamedPipeTransportOptions opzioni)`,
  `public static PipeSecurity Sicurezza()`,
  `public static void Ascolta(WebApplicationBuilder builder, string pipeName)`

**Attenzione a dove vivono le due impostazioni.** `PipeSecurity` e `CurrentUserOnly` stanno su
`NamedPipeTransportOptions`, che si raggiunge da `builder.WebHost.UseNamedPipes(...)` sul
**builder**, non da `KestrelServerOptions`. `ListenNamedPipe` sta invece su
`KestrelServerOptions`. Per questo `Ascolta` riceve il builder e fa entrambe le cose.

- [ ] **Step 1: scrivi il test che fallisce**

```csharp
using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using Observer.Service.LocalChannel;

namespace Observer.Service.Tests;

/// <summary>Il canale locale su Windows: la pipe si apre, convive col TCP, e la DACL e' quella voluta.</summary>
[Collection(AmbienteDelProcesso.Nome)]
public class CanaleLocaleWindowsTests
{
    internal static string NomeUnico() =>
        "observer-test-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    [SoloSuWindows]
    public void LaSicurezzaDellaPipeConcedeAgliInterattiviENonAdAuthenticatedUsers()
    {
        // Authenticated Users comprende ogni principal autenticato che raggiunga la macchina,
        // anche via SMB sulla porta 445. INTERACTIVE comprende solo chi ha una sessione qui.
        string sddl = WindowsNamedPipe.Sicurezza()
            .GetSecurityDescriptorSddlForm(AccessControlSections.Access);

        Assert.Contains(";;;IU)", sddl, StringComparison.Ordinal);
        Assert.DoesNotContain(";;;AU)", sddl, StringComparison.Ordinal);
    }

    [SoloSuWindows]
    public void CurrentUserOnlyRestaSpentoSOLOInsiemeAllaSicurezza()
    {
        // Regressione su un guasto che parte SENZA errori. Misurato: CurrentUserOnly = false
        // da solo produce una pipe con DACL (A;;FR;;;WD)(A;;FR;;;AN), cioe' leggibile da
        // Everyone e da ANONYMOUS LOGON, e l'host parte normalmente. Questo test esiste perche'
        // quel guasto non ha alcun sintomo visibile.
        Microsoft.AspNetCore.Server.Kestrel.Transport.NamedPipes.NamedPipeTransportOptions opzioni = new();

        WindowsNamedPipe.ConfiguraTrasporto(opzioni);

        Assert.False(opzioni.CurrentUserOnly);
        Assert.NotNull(opzioni.PipeSecurity);
    }

    [SoloSuWindows]
    public async Task PipeETcpConvivonoNelloStessoHostEServonoGliStessiEndpoint()
    {
        // La convivenza dei due trasporti e' la premessa dell'intero progetto: se
        // ListenNamedPipe sostituisse il trasporto socket invece di affiancarlo, servirebbero
        // due host e il piano cambierebbe forma.
        string pipe = NomeUnico();

        await using BancoKestrelReale banco = await BancoKestrelReale.AvviaAsync(
            opzioni =>
            {
                opzioni.Listen(IPAddress.Loopback, 0);
                opzioni.ListenNamedPipe(pipe);
            });

        Assert.Equal(2, banco.Indirizzi.Count);

        string tcp = banco.Indirizzi.Single(a => a.Contains("127.0.0.1", StringComparison.Ordinal));
        using HttpClient suTcp = new() { BaseAddress = new Uri(tcp) };
        Assert.Equal("pong", await suTcp.GetStringAsync("ping", CancellationToken.None));

        using HttpClient suPipe = BancoKestrelReale.ClientSu(HandlerVersoLaPipe(pipe));
        Assert.Equal("pong", await suPipe.GetStringAsync("ping", CancellationToken.None));
    }

    [SoloSuWindows]
    public async Task LaPipeAccettaPiuDiUnaConnessione()
    {
        // La PRIMA istanza si crea sempre: e' dalla seconda che serve FILE_CREATE_PIPE_INSTANCE,
        // e Kestrel ne apre piu' d'una. Una DACL che concede troppo poco fa fallire il bind con
        // il fuorviante "address already in use", quindi il caso da provare e' proprio questo.
        string pipe = NomeUnico();

        await using BancoKestrelReale banco = await BancoKestrelReale.AvviaAsync(
            opzioni => opzioni.ListenNamedPipe(pipe));

        for (int i = 0; i < 3; i++)
        {
            using HttpClient client = BancoKestrelReale.ClientSu(HandlerVersoLaPipe(pipe));
            Assert.Equal("pong", await client.GetStringAsync("ping", CancellationToken.None));
        }
    }

    internal static SocketsHttpHandler HandlerVersoLaPipe(
        string nome,
        TokenImpersonationLevel livello = TokenImpersonationLevel.Identification,
        string server = ".") =>
        new()
        {
            ConnectCallback = async (_, annulla) =>
            {
                // "." e NON "localhost": misurato, localhost passa da SMB e verrebbe
                // classificato come chiamante remoto.
                NamedPipeClientStream flusso = new(
                    server, nome, PipeDirection.InOut, PipeOptions.Asynchronous, livello);

                await flusso.ConnectAsync(annulla).ConfigureAwait(false);
                return flusso;
            },
        };
}
```

- [ ] **Step 2: guarda il test fallire**

Esegui: `dotnet test tests/Observer.Service.Tests --filter "FullyQualifiedName~CanaleLocaleWindowsTests"`
Atteso: errore di compilazione, `WindowsNamedPipe` non esiste.

- [ ] **Step 3: scrivi l'implementazione**

```csharp
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Transport.NamedPipes;

namespace Observer.Service.LocalChannel;

/// <summary>
/// L'ascolto su named pipe e la lista di chi puo' aprirla.
/// </summary>
/// <remarks>
/// Classe a parte e annotata perche' CA1416 con TreatWarningsAsErrors fa fallire la build su
/// ENTRAMBI i runner: e' analisi statica, non dipende dall'OS che compila. L'attributo su una
/// local function non viene onorato e non copre il corpo di una lambda, quindi il codice deve
/// stare qui e non nei top-level statements di Program.cs.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class WindowsNamedPipe
{
    /// <summary>Apre l'ascolto sulla pipe e ne configura il trasporto.</summary>
    /// <param name="builder">Il builder dell'applicazione.</param>
    /// <param name="pipeName">Il nome della pipe, senza prefisso.</param>
    public static void Ascolta(WebApplicationBuilder builder, string pipeName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        // UseNamedPipes NON serve per aprire la pipe: su Windows il trasporto e' gia'
        // registrato e ListenNamedPipe basta. Serve solo per queste opzioni.
        builder.WebHost.UseNamedPipes(ConfiguraTrasporto);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenNamedPipe(pipeName));
    }

    /// <summary>Imposta le due opzioni del trasporto. Insieme, mai una sola.</summary>
    /// <param name="opzioni">Le opzioni del trasporto named pipe.</param>
    public static void ConfiguraTrasporto(NamedPipeTransportOptions opzioni)
    {
        ArgumentNullException.ThrowIfNull(opzioni);

        // Le due righe seguenti vanno tenute ADIACENTI e non separate mai.
        // Impostare solo PipeSecurity fa lanciare all'avvio ArgumentException ("'pipeSecurity'
        // must be null when 'options' contains 'PipeOptions.CurrentUserOnly'"), ed e' il caso
        // innocuo perche' rumoroso. Impostare solo CurrentUserOnly = false e' quello
        // pericoloso: l'host parte e produce una pipe con DACL (A;;FR;;;WD)(A;;FR;;;AN), cioe'
        // leggibile da Everyone e da ANONYMOUS LOGON. Nessun errore, nessun warning.
        opzioni.CurrentUserOnly = false;
        opzioni.PipeSecurity = Sicurezza();
    }

    /// <summary>La DACL della pipe.</summary>
    /// <returns>Il descrittore da applicare al trasporto.</returns>
    public static PipeSecurity Sicurezza()
    {
        PipeSecurity sicurezza = new();

        // FullControl e non il solo CreateNewInstance: la prima istanza si crea sempre, ed e'
        // dalla SECONDA che serve FILE_CREATE_PIPE_INSTANCE (0x4). Kestrel ne apre piu' d'una,
        // e senza quel bit il bind fallisce con UnauthorizedAccessException, che Kestrel
        // traduce nel fuorviante "address already in use".
        sicurezza.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        sicurezza.AddAccessRule(new PipeAccessRule(
            WindowsIdentity.GetCurrent().User!,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        // INTERACTIVE e NON Authenticated Users: il secondo comprende ogni principal
        // autenticato capace di raggiungere la macchina, anche via SMB sulla porta 445.
        sicurezza.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));

        // Non serve ordinare le ACE a mano: PipeSecurity canonicalizza, e una DENY aggiunta
        // per ultima finisce comunque in testa (verificato confrontando le due SDDL, identiche
        // carattere per carattere). La garanzia e' pero' del tipo CommonAcl e NON della nostra
        // chiamata: importando un descrittore da SDDL o da forma binaria la DENY resterebbe
        // dove sta e diventerebbe inerte. Costruire sempre con AddAccessRule, mai importare.
        return sicurezza;
    }
}
```

- [ ] **Step 4: guarda i test passare**

Esegui: `dotnet test tests/Observer.Service.Tests --filter "FullyQualifiedName~CanaleLocaleWindowsTests"`
Atteso su Windows: quattro verdi. Su Linux: quattro `Ignorati`.

> Se il terzo test fallisce con `address already in use`, la causa **non** e' una collisione di
> nomi: e' la DACL che non concede abbastanza all'account che ospita la pipe. Rileggi il
> commento su `FullControl`.

- [ ] **Step 5: verifica e committa**

```bash
dotnet build -c Release && dotnet test --no-build -c Release
```

```bash
git add src/Observer.Service tests/Observer.Service.Tests && git commit -m "feat(service): ascolto su named pipe con una DACL esplicita"
```

---

### Task 4: classificare il chiamante su Windows

**Files:**
- Create: `src/Observer.Service/LocalChannel/CallerOrigin.cs`
- Create: `src/Observer.Service/LocalChannel/WindowsCallerIdentity.cs`
- Create: `src/Observer.Service/LocalChannel/LocalChannel.cs` (solo `Classifica`; `Configura`
  arriva nel task 6)
- Test: aggiunte a `tests/Observer.Service.Tests/CanaleLocaleWindowsTests.cs`

**Interfaces:**
- Consumes: `BancoKestrelReale.ClientSu`, `HandlerVersoLaPipe` dal task 3.
- Produces:
  - `internal enum CallerKind { NonIdentificabile = 0, ArrivatoDallaRete, LocaleIdentificato }`
  - `internal sealed record CallerOrigin(CallerKind Kind, string? Sid, string Diagnostica)`
  - `[SupportedOSPlatform("windows")] internal static partial class WindowsCallerIdentity` con
    `public static CallerOrigin Classifica(NamedPipeServerStream pipe)`
  - `internal static class LocalChannel` con `public static CallerOrigin Classifica(HttpContext contesto)`

Il valore **zero** dell'enum e' `NonIdentificabile`, cioe' un rifiuto: cosi' anche un campo
dimenticato o una struct non inizializzata negano.

- [ ] **Step 1: scrivi i test che falliscono**

```csharp
    [SoloSuWindows]
    public async Task ChiArrivaDalPuntoEClassificatoLocaleEIdentificato()
    {
        string pipe = NomeUnico();

        await using BancoKestrelReale banco = await BancoKestrelReale.AvviaAsync(
            opzioni => opzioni.ListenNamedPipe(pipe),
            app => app.MapGet("/chi", (HttpContext contesto) =>
            {
                CallerOrigin origine = LocalChannel.Classifica(contesto);
                return origine.Kind + "|" + (origine.Sid ?? "(nessuno)");
            }));

        using HttpClient client = BancoKestrelReale.ClientSu(HandlerVersoLaPipe(pipe));
        string esito = await client.GetStringAsync("chi", CancellationToken.None);

        Assert.StartsWith(nameof(CallerKind.LocaleIdentificato) + "|S-1-", esito, StringComparison.Ordinal);
    }

    [SoloSuWindows]
    public async Task ChiSceglieAnonymousNonEIdentificabile_ENonProduceUn500()
    {
        // Il livello di impersonation lo sceglie il CLIENT: con Anonymous la richiesta arriva
        // lo stesso ma il server non riesce a leggere il token. E' il caso di ATTACCO, non un
        // caso limite. Misurato: l'eccezione e' SecurityException con HRESULT 0x80070543, NON
        // IOException. Una guardia che cattura solo IOException lascia uscire un 500 proprio
        // sul percorso che si sta cercando di chiudere, e un 500 e' il segnale che dice a chi
        // sonda di aver toccato qualcosa.
        string pipe = NomeUnico();

        await using BancoKestrelReale banco = await BancoKestrelReale.AvviaAsync(
            opzioni => opzioni.ListenNamedPipe(pipe),
            app => app.MapGet("/chi", (HttpContext contesto) =>
                LocalChannel.Classifica(contesto).Kind.ToString()));

        using HttpClient client = BancoKestrelReale.ClientSu(
            HandlerVersoLaPipe(pipe, TokenImpersonationLevel.Anonymous));

        using HttpResponseMessage risposta = await client.GetAsync("chi", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, risposta.StatusCode);
        Assert.Equal(
            nameof(CallerKind.NonIdentificabile),
            await risposta.Content.ReadAsStringAsync(CancellationToken.None));
    }

    [SoloSuWindows]
    public async Task LocalhostNonEUnaViaLocale()
    {
        // Misurato: con serverName "localhost" GetNamedPipeClientComputerName RIESCE e
        // restituisce "[::1]", cioe' la connessione e' passata da SMB. Solo "." e' locale.
        // E' la trappola che farebbe perdere ore a chi scrive il client del piano 4.
        string pipe = NomeUnico();

        await using BancoKestrelReale banco = await BancoKestrelReale.AvviaAsync(
            opzioni => opzioni.ListenNamedPipe(pipe),
            app => app.MapGet("/chi", (HttpContext contesto) =>
                LocalChannel.Classifica(contesto).Kind.ToString()));

        using HttpClient client = BancoKestrelReale.ClientSu(
            HandlerVersoLaPipe(pipe, TokenImpersonationLevel.Identification, server: "localhost"));

        Assert.Equal(
            nameof(CallerKind.ArrivatoDallaRete),
            await client.GetStringAsync("chi", CancellationToken.None));
    }

    [SoloSuWindows]
    public async Task ChiArrivaDalTcpNonEMaiLocale()
    {
        string pipe = NomeUnico();

        await using BancoKestrelReale banco = await BancoKestrelReale.AvviaAsync(
            opzioni =>
            {
                opzioni.Listen(IPAddress.Loopback, 0);
                opzioni.ListenNamedPipe(pipe);
            },
            app => app.MapGet("/chi", (HttpContext contesto) =>
                LocalChannel.Classifica(contesto).Kind.ToString()));

        string tcp = banco.Indirizzi.Single(a => a.Contains("127.0.0.1", StringComparison.Ordinal));
        using HttpClient client = new() { BaseAddress = new Uri(tcp) };

        Assert.Equal(
            nameof(CallerKind.ArrivatoDallaRete),
            await client.GetStringAsync("chi", CancellationToken.None));
    }
```

- [ ] **Step 2: guarda i test fallire**

Esegui: `dotnet test tests/Observer.Service.Tests --filter "FullyQualifiedName~CanaleLocaleWindowsTests"`
Atteso: errore di compilazione, `CallerOrigin` e `LocalChannel` non esistono.

- [ ] **Step 3: scrivi `CallerOrigin.cs`**

```csharp
namespace Observer.Service.LocalChannel;

/// <summary>Come il servizio ha classificato chi sta chiamando.</summary>
/// <remarks>
/// Il valore ZERO e' <see cref="NonIdentificabile"/>, cioe' il caso che nega. Cosi' un campo
/// dimenticato, una struct non inizializzata o un ramo aggiunto per distrazione rifiutano
/// invece di concedere.
/// </remarks>
internal enum CallerKind
{
    /// <summary>Non si e' potuto stabilire chi sia. Rifiuto.</summary>
    NonIdentificabile = 0,

    /// <summary>Arrivato attraverso la rete: su Windows anche via SMB, non dalla macchina.</summary>
    ArrivatoDallaRete,

    /// <summary>Locale, e con un'identita' leggibile.</summary>
    LocaleIdentificato,
}

/// <summary>L'origine del chiamante, con la diagnosi che l'ha prodotta.</summary>
/// <param name="Kind">La classificazione.</param>
/// <param name="Sid">Il SID su Windows o l'uid su Linux, quando leggibile.</param>
/// <param name="Diagnostica">Perche' e' stata decisa cosi'. In inglese: finisce nei log.</param>
internal sealed record CallerOrigin(CallerKind Kind, string? Sid, string Diagnostica);
```

- [ ] **Step 4: scrivi `WindowsCallerIdentity.cs`**

```csharp
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;

namespace Observer.Service.LocalChannel;

/// <summary>
/// Stabilisce se il chiamante di una named pipe e' davvero locale, e chi e'.
/// </summary>
/// <remarks>
/// La domanda "sono locale?" NON si risponde guardando il trasporto: una named pipe e'
/// raggiungibile da remoto via SMB sulla porta 445. E non si risponde nemmeno guardando il
/// token: verso la macchina stessa Windows restituisce il token interattivo originale, con gli
/// stessi SID di gruppo della via locale, e il SID NETWORK assente in entrambi i casi.
/// <para>
/// Si risponde con GetNamedPipeClientComputerName, che fallisce con ERROR_PIPE_LOCAL quando la
/// connessione e' locale e riesce quando e' passata da SMB. Misurato su tre vie: "." locale,
/// indirizzo di rete remoto, "localhost" REMOTO — e funziona anche quando il token non e'
/// leggibile, cioe' proprio nel caso di attacco.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class WindowsCallerIdentity
{
    /// <summary>La connessione arriva dalla stessa macchina, non da SMB.</summary>
    private const int ErrorPipeLocal = 229;

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetNamedPipeClientComputerNameW",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientComputerName(
        nint pipe,
        ref char nome,
        uint lunghezzaInByte);

    /// <summary>Classifica il chiamante della pipe.</summary>
    /// <param name="pipe">Il flusso della connessione in corso.</param>
    /// <returns>L'origine del chiamante.</returns>
    public static CallerOrigin Classifica(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);

        Span<char> buffer = stackalloc char[256];

        bool riuscito = GetNamedPipeClientComputerName(
            pipe.SafePipeHandle.DangerousGetHandle(),
            ref MemoryMarshal.GetReference(buffer),
            (uint)(buffer.Length * sizeof(char)));

        int errore = Marshal.GetLastWin32Error();

        if (riuscito || errore != ErrorPipeLocal)
        {
            // Riuscito: la connessione e' passata da SMB. Fallito per un motivo diverso da
            // ERROR_PIPE_LOCAL: non sappiamo dire che sia locale, e nel dubbio non lo e'.
            return new CallerOrigin(
                CallerKind.ArrivatoDallaRete,
                null,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"GetNamedPipeClientComputerName ok={riuscito} win32={errore}"));
        }

        return LeggiIdentita(pipe);
    }

    private static CallerOrigin LeggiIdentita(NamedPipeServerStream pipe)
    {
        Cattura cattura = new();

        try
        {
            pipe.RunAsClient(cattura.Esegui);
        }
        catch (SecurityException ex)
        {
            // Il caso di ATTACCO: il client ha scelto TokenImpersonationLevel.Anonymous e si e'
            // reso unilateralmente non identificabile. HRESULT 0x80070543,
            // ERROR_BAD_IMPERSONATION_LEVEL. Senza questo catch il servizio risponde 500.
            return NonIdentificabile(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return NonIdentificabile(ex);
        }
        catch (IOException ex)
        {
            return NonIdentificabile(ex);
        }

        return cattura.Sid is { } sid
            ? new CallerOrigin(CallerKind.LocaleIdentificato, sid, "local caller identified")
            : new CallerOrigin(CallerKind.NonIdentificabile, null, "the caller token carried no user SID");
    }

    private static CallerOrigin NonIdentificabile(Exception ex) =>
        new(
            CallerKind.NonIdentificabile,
            null,
            string.Create(CultureInfo.InvariantCulture, $"{ex.GetType().Name} 0x{ex.HResult:X8}"));

    /// <summary>Il corpo eseguito sotto impersonation.</summary>
    /// <remarks>
    /// Un metodo di istanza di una classe annotata, e NON una lambda: l'attributo
    /// [SupportedOSPlatform] non copre il corpo di una lambda e CA1416 farebbe fallire la
    /// build. Passato a RunAsClient come gruppo di metodi.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private sealed class Cattura
    {
        public string? Sid { get; private set; }

        public void Esegui() =>
            Sid = WindowsIdentity.GetCurrent(ifImpersonating: true)?.User?.Value;
    }
}
```

- [ ] **Step 5: scrivi `LocalChannel.Classifica`**

```csharp
using System.Net.Sockets;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;

namespace Observer.Service.LocalChannel;

/// <summary>Il canale locale, visto da codice che non sa su quale sistema gira.</summary>
internal static class LocalChannel
{
    /// <summary>Classifica chi ha mandato questa richiesta.</summary>
    /// <param name="contesto">La richiesta in corso.</param>
    /// <returns>L'origine del chiamante.</returns>
    public static CallerOrigin Classifica(HttpContext contesto)
    {
        ArgumentNullException.ThrowIfNull(contesto);

        // Le due feature sono mutuamente esclusive e affidabili come INSTRADAMENTO: misurato,
        // sulla pipe c'e' solo la prima e sul socket solo la seconda. Ma dicono da DOVE e'
        // entrata la richiesta, NON se il chiamante sia ammesso: quella e' la riga sbagliata
        // che la specifica documenta, e non va scritta qui ne' altrove.
        if (OperatingSystem.IsWindows()
            && contesto.Features.Get<IConnectionNamedPipeFeature>() is { } pipe)
        {
            return WindowsCallerIdentity.Classifica(pipe.NamedPipe);
        }

        if (OperatingSystem.IsLinux()
            && contesto.Features.Get<IConnectionSocketFeature>() is { } presa
            && presa.Socket.AddressFamily == AddressFamily.Unix)
        {
            return LinuxCallerIdentity.Classifica(presa.Socket);
        }

        return new CallerOrigin(CallerKind.ArrivatoDallaRete, null, "the request arrived over TCP");
    }
}
```

> Il riferimento a `LinuxCallerIdentity` non compila fino al task 6. Fino ad allora sostituisci
> quel ramo con `return new CallerOrigin(CallerKind.NonIdentificabile, null, "not implemented yet");`
> e **lascia un TODO nel messaggio di commit, non nel codice**.

- [ ] **Step 6: guarda i test passare**

Esegui: `dotnet test tests/Observer.Service.Tests --filter "FullyQualifiedName~CanaleLocaleWindowsTests"`

Se `ref char` non compila con `[LibraryImport]`, **non ripiegare su `[DllImport]`**: la forma
verificata e' il parametro dichiarato `ref char` chiamato con
`ref MemoryMarshal.GetReference(buffer)`. In ultima istanza aggiungi
`<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` al `.csproj`, come gia' fa `Observer.Core`.

- [ ] **Step 7: verifica e committa**

```bash
dotnet build -c Release && dotnet test --no-build -c Release
```

```bash
git add src/Observer.Service tests/Observer.Service.Tests && git commit -m "feat(service): classifica il chiamante della named pipe, con rifiuto predefinito"
```

---

### Task 5: socket unix su Linux

**Files:**
- Create: `src/Observer.Service/LocalChannel/LinuxUnixSocket.cs`
- Test: `tests/Observer.Service.Tests/CanaleLocaleLinuxTests.cs`

**Interfaces:**
- Consumes: `EndpointUrl`, `BancoKestrelReale`, `SoloSuLinuxAttribute`.
- Produces: `[SupportedOSPlatform("linux")] internal static class LinuxUnixSocket` con
  `public static void PreparaPercorso(string percorso)`,
  `public static Task<bool> BonificaSocketOrfanoAsync(string percorso, TimeSpan attesa)`,
  `public static void RestringiAlProprietario(string percorso)`

Ordine vincolato, e non e' negoziabile: **convalida + directory + bonifica PRIMA di `Build()`;
il modo del file del socket DOPO `StartAsync()`**, perche' prima quel file non esiste.

- [ ] **Step 1: scrivi i test che falliscono**

```csharp
using System.Net.Sockets;
using Observer.Service.LocalChannel;

namespace Observer.Service.Tests;

/// <summary>Il canale locale su Linux.</summary>
[Collection(AmbienteDelProcesso.Nome)]
public class CanaleLocaleLinuxTests
{
    [SoloSuLinux]
    public async Task IlSocketUnixServeGliStessiEndpointDelTcp()
    {
        string percorso = PercorsoBreve();

        await using BancoKestrelReale banco = await BancoKestrelReale.AvviaAsync(
            opzioni => opzioni.ListenUnixSocket(percorso));

        using HttpClient client = BancoKestrelReale.ClientSu(HandlerVersoIlSocket(percorso));

        Assert.Equal("pong", await client.GetStringAsync("ping", CancellationToken.None));
    }

    [SoloSuLinux]
    public async Task UnaChiusuraPulitaCancellaIlFileDelSocket()
    {
        // Contro l'idea diffusa che su Linux il file sopravviva sempre: .NET fa l'unlink
        // esplicito, perche' UnixDomainSocketEndPoint porta un boundFileName. Verificato su
        // Linux vero. La bonifica serve SOLO dopo una morte violenta.
        string percorso = PercorsoBreve();

        await using (BancoKestrelReale banco = await BancoKestrelReale.AvviaAsync(
            opzioni => opzioni.ListenUnixSocket(percorso)))
        {
            Assert.True(File.Exists(percorso));
        }

        Assert.False(File.Exists(percorso));
    }

    [SoloSuLinux]
    public async Task LaBonificaNonRubaIlSocketAUnIstanzaViva()
    {
        // "Se il file esiste, cancellalo" permette a una seconda istanza di scippare il socket
        // a una prima istanza sana. La bonifica deve sondare, e sondare con un TIMEOUT: un
        // Connect() bloccante contro un listener vivo con la coda di accept piena resta appeso
        // indefinitamente — misurato oltre venti secondi — e sotto systemd diventa un timeout
        // di avvio senza alcuna diagnosi.
        string percorso = PercorsoBreve();

        await using BancoKestrelReale vivo = await BancoKestrelReale.AvviaAsync(
            opzioni => opzioni.ListenUnixSocket(percorso));

        bool bonificato = await LinuxUnixSocket.BonificaSocketOrfanoAsync(
            percorso, TimeSpan.FromMilliseconds(500));

        Assert.False(bonificato);
        Assert.True(File.Exists(percorso));
    }

    [SoloSuLinux]
    public void IlModoDellaDirectoryVieneImpostatoAncheSeLaDirectoryEsisteGia()
    {
        // Directory.CreateDirectory(percorso, modo) NON applica il modo a una directory che
        // esiste gia': misurato, e' un no-op silenzioso. Quindi la protezione non esiste dal
        // secondo avvio in poi, ne' su una /run/observer creata da systemd col suo 0755.
        string cartella = Path.Combine(Path.GetTempPath(), "obs-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(cartella);
        File.SetUnixFileMode(
            cartella,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);

        try
        {
            LinuxUnixSocket.PreparaPercorso(Path.Combine(cartella, "o.sock"));

            UnixFileMode modo = File.GetUnixFileMode(cartella);

            Assert.Equal(UnixFileMode.None, modo & UnixFileMode.OtherRead);
            Assert.Equal(UnixFileMode.None, modo & UnixFileMode.OtherWrite);
        }
        finally
        {
            Directory.Delete(cartella, recursive: true);
        }
    }

    internal static string PercorsoBreve()
    {
        // Il limite e' 107 BYTE per l'intero percorso, e il temp di un runner di CI puo'
        // essere lungo: il percorso viene verificato, non sperato.
        string percorso = Path.Combine(
            Path.GetTempPath(),
            "o-" + Guid.NewGuid().ToString("N")[..8] + ".sock");

        Assert.Null(EndpointUrl.Problema("http://unix:" + percorso));

        return percorso;
    }

    internal static SocketsHttpHandler HandlerVersoIlSocket(string percorso) =>
        new()
        {
            ConnectCallback = async (_, annulla) =>
            {
                Socket presa = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await presa.ConnectAsync(new UnixDomainSocketEndPoint(percorso), annulla).ConfigureAwait(false);
                return new NetworkStream(presa, ownsSocket: true);
            },
        };
}
```

- [ ] **Step 2: guarda i test fallire**

Su Windows saranno **saltati**, ed e' l'esito giusto: e' cio' che prova che l'attributo di salto
funziona.

Esegui: `dotnet test tests/Observer.Service.Tests --filter "FullyQualifiedName~CanaleLocaleLinuxTests"`
Atteso su Windows: `Ignorati: 4`. Il rosso vero arriva da `build (ubuntu-latest)` in CI.

- [ ] **Step 3: scrivi l'implementazione**

```csharp
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace Observer.Service.LocalChannel;

/// <summary>Preparazione e bonifica del socket unix.</summary>
/// <remarks>
/// L'ordine e' vincolato: convalida, directory e bonifica PRIMA di costruire l'host; il modo
/// del file del socket DOPO l'avvio, perche' prima quel file non esiste.
/// </remarks>
[SupportedOSPlatform("linux")]
internal static class LinuxUnixSocket
{
    private const UnixFileMode ModoDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute;

    private const UnixFileMode ModoSocket =
        UnixFileMode.UserRead | UnixFileMode.UserWrite |
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite;

    /// <summary>Crea la directory del socket e le impone il modo giusto.</summary>
    /// <param name="percorso">Il percorso completo del socket.</param>
    public static void PreparaPercorso(string percorso)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(percorso);

        string? cartella = Path.GetDirectoryName(percorso);

        if (string.IsNullOrEmpty(cartella))
        {
            return;
        }

        Directory.CreateDirectory(cartella, ModoDirectory);

        // La riga precedente NON applica il modo a una directory che esiste gia': verificato,
        // e' un no-op silenzioso. Senza questa seconda riga la protezione non esiste dal
        // secondo avvio in poi, ne' su una /run/observer creata da systemd.
        File.SetUnixFileMode(cartella, ModoDirectory);
    }

    /// <summary>Cancella il file del socket SOLO se nessuno sta ascoltando.</summary>
    /// <param name="percorso">Il percorso del socket.</param>
    /// <param name="attesa">Quanto aspettare la risposta della sonda.</param>
    /// <returns>Vero se il file e' stato rimosso.</returns>
    public static async Task<bool> BonificaSocketOrfanoAsync(string percorso, TimeSpan attesa)
    {
        if (!File.Exists(percorso))
        {
            return false;
        }

        using Socket sonda = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using CancellationTokenSource scadenza = new(attesa);

        try
        {
            // ConnectAsync con timeout e NON Connect(): contro un listener vivo con la coda di
            // accept piena, connect(2) su AF_UNIX non rifiuta, aspetta. Misurato: oltre venti
            // secondi appeso senza decidere ne' "vivo" ne' "morto".
            await sonda.ConnectAsync(new UnixDomainSocketEndPoint(percorso), scadenza.Token)
                .ConfigureAwait(false);

            // Qualcuno ha risposto: il socket e' vivo, e cancellarlo lo scippirebbe a
            // un'istanza sana.
            return false;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            File.Delete(percorso);
            return true;
        }
        catch (OperationCanceledException)
        {
            // Scaduta la sonda: non sappiamo se sia vivo. Nel dubbio NON si cancella.
            return false;
        }
    }

    /// <summary>Impone il modo del file del socket. Da chiamare DOPO StartAsync.</summary>
    /// <param name="percorso">Il percorso del socket.</param>
    public static void RestringiAlProprietario(string percorso) =>
        // connect(2) su AF_UNIX richiede il bit di SCRITTURA, non di lettura: un modo che
        // concede solo la lettura al gruppo chiude fuori esattamente chi deve entrare.
        File.SetUnixFileMode(percorso, ModoSocket);
}
```

- [ ] **Step 4: verifica e committa**

Su Windows i test restano saltati; il verde vero arriva da `build (ubuntu-latest)`.

```bash
dotnet build -c Release && dotnet test --no-build -c Release
```

```bash
git add src/Observer.Service tests/Observer.Service.Tests && git commit -m "feat(service): socket unix con bonifica che non scippa un'istanza viva"
```

---

### Task 6: `SO_PEERCRED`, opzioni e cablaggio finale

**Files:**
- Create: `src/Observer.Service/LocalChannel/LinuxCallerIdentity.cs`
- Create: `src/Observer.Service/LocalChannel/LocalChannelOptions.cs`
- Modify: `src/Observer.Service/LocalChannel/LocalChannel.cs`, `src/Observer.Service/Program.cs`,
  `src/Observer.Service/appsettings.json`
- Test: aggiunte a `CanaleLocaleLinuxTests.cs`

**Interfaces:**
- Consumes: tutto quanto sopra.
- Produces:
  - `[SupportedOSPlatform("linux")] internal static class LinuxCallerIdentity` con
    `public static CallerOrigin Classifica(Socket presa)`
  - `internal sealed class LocalChannelOptions` con
    `public const string SectionName = "Observer:LocalChannel";`,
    `public bool Enabled { get; set; } = true;`, `public string PipeName { get; set; } = "Observer";`,
    `public string SocketPath { get; set; } = "/run/observer/observer.sock";`,
    `public void Validate()`
  - `LocalChannel.Configura(WebApplicationBuilder builder, LocalChannelOptions opzioni)`

- [ ] **Step 1: scrivi il test che fallisce**

```csharp
    [SoloSuLinux]
    public async Task IlChiamanteSuSocketUnixVieneIdentificatoDalSuoUid()
    {
        string percorso = PercorsoBreve();

        await using BancoKestrelReale banco = await BancoKestrelReale.AvviaAsync(
            opzioni => opzioni.ListenUnixSocket(percorso),
            app => app.MapGet("/chi", (HttpContext contesto) =>
            {
                CallerOrigin origine = LocalChannel.Classifica(contesto);
                return origine.Kind + "|" + (origine.Sid ?? "(nessuno)");
            }));

        using HttpClient client = BancoKestrelReale.ClientSu(HandlerVersoIlSocket(percorso));
        string esito = await client.GetStringAsync("chi", CancellationToken.None);

        // Su un socket unix il chiamante e' SEMPRE sulla stessa macchina: non esiste la via
        // SMB che c'e' su Windows. L'unica domanda e' se l'uid sia leggibile.
        string[] parti = esito.Split('|');

        Assert.Equal(nameof(CallerKind.LocaleIdentificato), parti[0]);
        Assert.True(uint.TryParse(parti[1], out _), $"uid non numerico: {parti[1]}");
    }
```

- [ ] **Step 2: guarda il test fallire**

Su Windows: saltato. In CI su Linux: errore di compilazione, poi rosso.

- [ ] **Step 3: scrivi la lettura di `SO_PEERCRED`**

```csharp
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Observer.Service.LocalChannel;

/// <summary>Identifica il chiamante di un socket unix leggendo le credenziali del peer.</summary>
/// <remarks>
/// La mappatura .NET di SO_PEERCRED non esiste, quindi si passa da GetRawSocketOption con i
/// valori numerici di Linux. Sono valori di LINUX e non di POSIX: su altri unix cambiano.
/// </remarks>
[SupportedOSPlatform("linux")]
internal static class LinuxCallerIdentity
{
    private const int SolSocket = 1;
    private const int SoPeerCred = 17;

    /// <summary>struct ucred = { int32 pid; uint32 uid; uint32 gid; }, 12 byte.</summary>
    private const int ByteDiUcred = 12;

    /// <summary>Classifica il chiamante del socket.</summary>
    /// <param name="presa">Il socket accettato.</param>
    /// <returns>L'origine del chiamante.</returns>
    public static CallerOrigin Classifica(Socket presa)
    {
        ArgumentNullException.ThrowIfNull(presa);

        Span<byte> buffer = stackalloc byte[ByteDiUcred];

        try
        {
            int scritti = presa.GetRawSocketOption(SolSocket, SoPeerCred, buffer);

            if (scritti != ByteDiUcred)
            {
                return new CallerOrigin(
                    CallerKind.NonIdentificabile,
                    null,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"SO_PEERCRED returned {scritti} bytes instead of {ByteDiUcred}"));
            }
        }
        catch (SocketException ex)
        {
            return new CallerOrigin(
                CallerKind.NonIdentificabile,
                null,
                string.Create(CultureInfo.InvariantCulture, $"SO_PEERCRED failed: {ex.SocketErrorCode}"));
        }

        // MemoryMarshal.Read e NON BinaryPrimitives.Read*LittleEndian: la struct e' in ordine
        // NATIVO, e forzare little-endian sarebbe sbagliato su una macchina big-endian.
        uint uid = MemoryMarshal.Read<uint>(buffer[4..]);

        return new CallerOrigin(
            CallerKind.LocaleIdentificato,
            uid.ToString(CultureInfo.InvariantCulture),
            "local caller identified by SO_PEERCRED");
    }
}
```

Poi rimetti in `LocalChannel.Classifica` il ramo Linux vero, al posto del segnaposto del task 4.

- [ ] **Step 4: scrivi le opzioni e il cablaggio**

```csharp
namespace Observer.Service.LocalChannel;

/// <summary>Dove sta il canale locale su questa macchina.</summary>
/// <remarks>
/// Nome della pipe e percorso del socket sono CONFIGURABILI, e non e' una comodita': un
/// endpoint che non si binda abbatte l'INTERO host, endpoint TCP compreso. Con valori fissi,
/// lanciare "dotnet run --project src/Observer.Service" su una macchina dove il servizio
/// installato gira non fallirebbe piu' "solo sulla porta": non partirebbe affatto.
/// </remarks>
internal sealed class LocalChannelOptions
{
    /// <summary>Il percorso della sezione in configurazione.</summary>
    public const string SectionName = "Observer:LocalChannel";

    /// <summary>Se aprire il canale locale.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Il nome della named pipe su Windows, senza prefisso.</summary>
    public string PipeName { get; set; } = "Observer";

    /// <summary>Il percorso del socket unix su Linux.</summary>
    public string SocketPath { get; set; } = "/run/observer/observer.sock";

    /// <summary>Si rifiuta di partire con valori inutilizzabili.</summary>
    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(PipeName))
        {
            throw new InvalidOperationException(
                $"{SectionName}:PipeName is empty. Give it a name or set Enabled to false.");
        }

        if (OperatingSystem.IsLinux()
            && EndpointUrl.Problema("http://unix:" + SocketPath) is { } problema)
        {
            throw new InvalidOperationException($"{SectionName}:SocketPath can't be used. {problema}");
        }
    }
}
```

In `appsettings.json`, dentro `"Observer"`:

```json
    "LocalChannel": {
      "Enabled": true,
      "PipeName": "Observer",
      "SocketPath": "/run/observer/observer.sock"
    }
```

In `Program.cs`, dopo la convalida degli endpoint del task 1 e **prima** di `builder.Build()`:

```csharp
LocalChannelOptions canaleLocale =
    builder.Configuration.GetSection(LocalChannelOptions.SectionName).Get<LocalChannelOptions>()
        ?? new LocalChannelOptions();

canaleLocale.Validate();

LocalChannel.Configura(builder, canaleLocale);
```

`LocalChannel.Configura` chiama `WindowsNamedPipe.Ascolta(builder, opzioni.PipeName)` dentro
`if (OperatingSystem.IsWindows())`, e su Linux fa nell'ordine: `PreparaPercorso`,
`BonificaSocketOrfanoAsync`, poi `ConfigureKestrel(k => k.ListenUnixSocket(...))`. La chiamata a
`RestringiAlProprietario` va dopo l'avvio: registrala su
`IHostApplicationLifetime.ApplicationStarted`.

- [ ] **Step 5: gestisci il caso `dotnet run` non-root su Linux**

`/run/observer` non e' creabile da un utente normale, e meta' della CI e' Linux. Quando
`PreparaPercorso` fallisce con `UnauthorizedAccessException`, ripiega su
`$XDG_RUNTIME_DIR/observer/observer.sock`, e se manca anche quello su
`Path.GetTempPath()`. Scrivi una riga di log che dice **quale** percorso e' stato usato:
altrimenti il client del piano 4 cerchera' nel posto sbagliato senza capire perche'.

- [ ] **Step 6: verifica finale, e quella che nessun test puo' fare**

```bash
dotnet build -c Release && dotnet test --no-build -c Release
```

Attesi: **0 avvisi, 0 errori**, suite verde. Su Windows i test Linux risultano `Ignorati` e
viceversa: e' l'esito corretto, non copertura mancante.

Poi, a mano:

```bash
dotnet run --project src/Observer.Service
```

Nel log di avvio devono comparire **due** righe `Now listening on:` — una `http://0.0.0.0:5057`
e una `http://pipe:/Observer` (o `http://unix:/...` su Linux). Se ne compare una sola, il canale
locale non e' stato aperto e **nessun test se ne accorgerebbe**.

Verifica infine che il comportamento non sia cambiato: una richiesta sulla pipe **senza** token
deve rispondere `401`, esattamente come oggi sul TCP.

- [ ] **Step 7: committa e apri la PR**

```bash
git add src/Observer.Service tests/Observer.Service.Tests && git commit -m "feat(service): canale locale su named pipe e socket unix, con il chiamante identificato"
```

```bash
gh pr create --fill && gh pr merge --auto --merge --delete-branch
```

---

## Cosa questo piano NON chiude

Va detto qui perche' chi lo esegue non lo scopra credendo di aver finito.

1. **L'autorizzazione non cambia.** Il token resta obbligatorio ovunque. E' il piano 2.
2. ~~**La combinazione LocalSystem + GUI dell'utente non e' mai stata eseguita.**~~
   **CHIUSA il 2026-08-27**, installando il servizio davvero con
   `scripts/servizio-windows.ps1` e interrogandolo da una sessione non elevata: la pipe si
   apre, una GET senza token risponde `200`, il TCP senza token resta `401`, e il
   campionamento produce valori veri in Session 0.
3. **La DACL non e' stata provata contro un chiamante SMB da una SECONDA macchina.** Verso se'
   stessa Windows restituisce il token interattivo originale, quindi questa macchina non e' un
   banco valido per quella verifica. Il discriminante `GetNamedPipeClientComputerName` non ne ha
   bisogno; la DACL si'.
4. **Il banco di prova non applica la DACL.** `BancoKestrelReale` chiama `ListenNamedPipe` ma
   non `UseNamedPipes`, quindi nei test il trasporto resta col suo `CurrentUserOnly = true` e i
   client entrano perche' sono lo stesso utente non elevato del server. La `PipeSecurity` e'
   verificata come **dato** (la sua SDDL, e le due opzioni impostate insieme), non come
   **effetto**. Provare l'effetto richiede due account distinti sulla stessa macchina, e va
   fatto a mano: e' la stessa lacuna del punto 2.
4. **La riconnessione quando il servizio si riavvia e la pipe sparisce** non e' esercitata da
   nessun test. E' il caso normale di un aggiornamento, e `MainViewModel` ha gia' un percorso di
   riconnessione da rispettare.
