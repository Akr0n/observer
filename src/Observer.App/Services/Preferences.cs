using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Styling;

namespace Observer.App.Services;

/// <summary>Dove stava la finestra l'ultima volta.</summary>
/// <param name="X">Bordo sinistro, in pixel fisici dello screen.</param>
/// <param name="Y">Bordo superiore, in pixel fisici dello screen.</param>
/// <param name="Width">Larghezza, in pixel logici (quelli con cui la finestra si misura).</param>
/// <param name="Height">Altezza, in pixel logici.</param>
/// <param name="Maximized">True se era a tutto screen: allora X, Y e le misure sono quelle di prima.</param>
/// <remarks>
/// Posizione fisica e misure logiche, e non e' un'incoerenza: e' come Avalonia le espone
/// (<c>Position</c> e' un <c>PixelPoint</c>, <c>Width</c> e' in unita' indipendenti dal DPI),
/// e convertire da una parte all'altra con la zoom dello screen di ieri darebbe una finestra
/// di misura diversa il giorno in cui la zoom cambia.
/// </remarks>
public sealed record WindowPlacement(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("maximized")] bool Maximized)
{
    /// <summary>Quanto della finestra deve stare dentro uno screen perche' la si possa afferrare.</summary>
    /// <remarks>
    /// Un quadrato di 120 pixel fisici a partire dall'angolo in alto a sinistra: ci sta dentro
    /// l'icona e un pezzo di barra del titolo, cioe' il minimo per trascinarla via se il resto
    /// e' fuori. Il caso che questo evita e' un monitor scollegato: senza controllo la finestra
    /// riaprirebbe su uno screen che non c'e' piu', invisibile e senza modo di raggiungerla.
    /// </remarks>
    public const int MinimumGrabbableSize = 120;

    /// <summary>Di quanto il bordo sinistro puo' sporgere fuori dallo screen.</summary>
    /// <remarks>
    /// Windows disegna attorno a ogni finestra un bordo invisibile di 7-8 pixel, e una
    /// finestra agganciata al bordo sinistro (Win+Freccia) sta a X = -8: senza questa
    /// tolleranza non verrebbe mai ricordata. In alto no: il bordo invisibile in alto non
    /// c'e', e una finestra maximized sta a (-8, -8), che cosi' resta esclusa.
    /// </remarks>
    public const int LeftEdgeTolerance = 16;

    /// <summary>Una posizione che porta solo lo stato: non passa <see cref="WithinAnyOf"/>.</summary>
    private static WindowPlacement StateOnlyPlacement => new(0, 0, 0, 0, Maximized: false);

    /// <summary>Un'area di lavoro, in pixel fisici.</summary>
    /// <param name="X">Bordo sinistro.</param>
    /// <param name="Y">Bordo superiore.</param>
    /// <param name="Width">Larghezza.</param>
    /// <param name="Height">Altezza.</param>
    public readonly record struct WorkArea(int X, int Y, int Width, int Height);

    /// <summary>Questa posizione, se sta su uno degli screens di adesso; altrimenti null.</summary>
    /// <param name="screens">Le aree di lavoro degli screens collegati.</param>
    /// <returns>Se stessa, oppure null quando la finestra riaprirebbe fuori da tutto.</returns>
    public WindowPlacement? WithinAnyOf(IReadOnlyList<WorkArea> screens)
    {
        ArgumentNullException.ThrowIfNull(screens);

        if (Width < MinimumGrabbableSize || Height < MinimumGrabbableSize)
        {
            return null;
        }

        foreach (WorkArea screen in screens)
        {
            // Le costanti si sommano e sottraggono dal lato dello screen, MAI da X o Y:
            // con un file scritto a mano che dice x = 2147483647 la somma traboccava, il
            // confronto passava, e la finestra si apriva invisibile - e si risalvava
            // identica a ogni chiusura.
            if (X >= screen.X - LeftEdgeTolerance
                && Y >= screen.Y
                && X <= screen.X + screen.Width - MinimumGrabbableSize
                && Y <= screen.Y + screen.Height - MinimumGrabbableSize)
            {
                return this;
            }
        }

        return null;
    }

    /// <summary>Cosa ricordare alla chiusura, a seconda di com'e' la finestra.</summary>
    /// <param name="minimized">True se la finestra e' ridotta a icona.</param>
    /// <param name="maximized">
    /// True se e' a tutto screen, oppure se lo era prima di essere ridotta a icona.
    /// </param>
    /// <param name="lastNormal">
    /// L'ultima geometria vista in stato normal durante questa sessione, se c'e' stata.
    /// </param>
    /// <param name="saved">La geometria letta dal file all'avvio, se c'era.</param>
    /// <param name="current">La geometria di adesso, che vale solo a finestra normal.</param>
    /// <returns>La posizione da scrivere, oppure null se non c'e' niente di sensato da dire.</returns>
    /// <remarks>
    /// Le misure di una finestra a tutto screen sono quelle dello screen, e la posizione di
    /// una ridotta a icona e' fuori da ogni screen: in quei due stati si ricorda l'ultima
    /// geometria normal di QUESTA sessione, non quella letta dal file all'avvio - che e'
    /// cio' che si faceva prima, e uno spostamento fatto prima di massimizzare andava
    /// perso: su due monitor la finestra riapriva su quello sbagliato. Se nessuna geometria
    /// normal e' nota, lo stato a tutto screen si ricorda da solo, con una posizione che
    /// <see cref="WithinAnyOf"/> scarta: la finestra si apre dove decide il sistema, ma piena.
    /// </remarks>
    public static WindowPlacement? AtClose(
        bool minimized,
        bool maximized,
        WindowPlacement? lastNormal,
        WindowPlacement? saved,
        WindowPlacement current)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (!minimized && !maximized)
        {
            return current with { Maximized = false };
        }

        WindowPlacement? normal = lastNormal ?? saved;

        if (maximized)
        {
            return (normal ?? StateOnlyPlacement) with { Maximized = true };
        }

        return normal is null ? null : normal with { Maximized = false };
    }
}

/// <summary>Una voce del selettore dello zoom.</summary>
/// <param name="Factor">La zoom: 1 e' la misura normal.</param>
/// <remarks>
/// Il testo della voce E' il suo <see cref="ToString"/>: un lettore di screen annuncia
/// quello, e con un double nudo annunciava "1,15" al posto di "115 %". L'uguaglianza per
/// valore del record e' cio' che fa ritrovare la voce a partire dal numero.
/// </remarks>
public sealed record ZoomOption(double Factor)
{
    /// <inheritdoc />
    public override string ToString() => Factor.ToString("P0", CultureInfo.CurrentCulture);
}

/// <summary>Una voce del selettore del period dello storico.</summary>
/// <param name="Key">Cio' che va nel file: <c>1h</c>, <c>24h</c> o <c>7d</c>.</param>
/// <remarks>
/// Il passo della barra non e' quello della sorgente, e i due numeri rispondono a domande
/// diverse. La SORGENTE e' cio' che il servizio conserva: campioni al minuto per sette giorni,
/// a cinque minuti per novanta. Il PASSO della barra e' quanto largo dev'essere un intervallo
/// perche' la striscia ci stia: circa novanta barre su ottocento pixel danno barrette da nove,
/// che e' il minimo per vederle separate. From qui la tabella: un'ora a un minuto fa sessanta
/// barre, un giorno a un quarto d'ora ne fa novantasei, una settimana a due ore ottantaquattro.
/// <para>
/// Novanta giorni NON c'e', anche se il servizio li conserva: a cinque minuti sarebbero 25 920
/// punti, oltre il tetto che il servizio impone a una risposta, e allargando la barra fino a
/// farceli stare la striscia direbbe una cosa sola per ogni giorno e mezzo. Un grafico che
/// mente e' peggio di un grafico che manca.
/// </para>
/// </remarks>
public sealed record HistoryPeriodOption(string Key)
{
    /// <inheritdoc />
    public override string ToString() => Key switch
    {
        "24h" => "24 hours",
        "7d" => "7 days",
        _ => "1 hour",
    };

    /// <summary>Quanto storico mostra la striscia.</summary>
    public TimeSpan Duration => Key switch
    {
        "24h" => TimeSpan.FromHours(24),
        "7d" => TimeSpan.FromDays(7),
        _ => TimeSpan.FromHours(1),
    };

    /// <summary>Quanto dura un intervallo della striscia.</summary>
    public TimeSpan Step => Key switch
    {
        "24h" => TimeSpan.FromMinutes(15),
        "7d" => TimeSpan.FromHours(2),
        _ => TimeSpan.FromMinutes(1),
    };

    /// <summary>La risoluzione da chiedere al servizio.</summary>
    public string Resolution => Key switch
    {
        "1h" => "1m",
        _ => "5m",
    };

    /// <summary>Quanto dura un endpoint della sorgente, per allineare la coda grezza.</summary>
    public TimeSpan SourceStep => Key switch
    {
        "1h" => TimeSpan.FromMinutes(1),
        _ => TimeSpan.FromMinutes(5),
    };

    /// <summary>Il titolo sopra la striscia.</summary>
    public string Title => Key switch
    {
        "24h" => "Last 24 hours",
        "7d" => "Last 7 days",
        _ => "Last hour",
    };

    /// <summary>Quante barre ha la striscia.</summary>
    public int BarCount => (int)(Duration / Step);
}

/// <summary>Una voce del selettore del theme: quello del sistema, chiaro o scuro.</summary>
/// <param name="Key">Cio' che va nel file: <c>system</c>, <c>light</c> o <c>dark</c>.</param>
/// <remarks>
/// Come <see cref="ZoomOption"/>: il testo della voce e' il suo <see cref="ToString"/>, ed e'
/// cio' che la tendina mostra e che un lettore di screen annuncia.
/// </remarks>
public sealed record ThemeOption(string Key)
{
    /// <inheritdoc />
    public override string ToString() => Key switch
    {
        "light" => "Light",
        "dark" => "Dark",
        _ => "System",
    };

    /// <summary>La variante di theme per una key: quella predefinita segue il sistema.</summary>
    /// <param name="key">La key, gia' ammessa o no.</param>
    /// <returns>La variante da chiedere all'applicazione.</returns>
    /// <remarks>
    /// E' l'unico ramo con una decisione vera: un refuso qui non farebbe rumore, lascerebbe
    /// solo una finestra chiara a chi ha chiesto quella scura. Per questo e' provato a parte.
    /// </remarks>
    public static ThemeVariant VariantFor(string key) => key switch
    {
        "light" => ThemeVariant.Light,
        "dark" => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };
}

/// <summary>Cio' che la dashboard ricorda di se' fra un avvio e l'altro.</summary>
/// <param name="Placement">Dove stava la finestra, oppure null se non lo sa ancora.</param>
/// <param name="Zoom">Quanto e' scalata la finestra: 1 e' la misura normal, sotto 1 e'
/// piu' piccola.</param>
/// <param name="Theme">Il theme scelto: <c>system</c>, <c>light</c> o <c>dark</c>.</param>
/// <param name="MachineName">
/// Il name della macchina che si stava guardando, o null per questo computer.
/// </param>
/// <param name="HistoryPeriod">Quanto storico mostra la striscia: <c>1h</c>, <c>24h</c> o <c>7d</c>.</param>
/// <remarks>
/// Un file a parte e non <c>client.json</c>: quello porta una credenziale, e un programma che lo
/// riscrivesse a ogni chiusura per salvare qualche preferenza sarebbe un programma che riscrive una
/// credenziale a ogni chiusura. Sono TUTTI parametri posizionali senza valore predefinito, di
/// proposito: chi costruisce le preferences deve dirli tutti, e un
/// <c>new Preferences(posizione, zoom)</c> che ne dimentica uno non compila — che e' come si
/// scopre, il giorno che se ne aggiunge un altro, ogni endpoint da aggiornare.
/// </remarks>
public sealed record Preferences(
    [property: JsonPropertyName("window")] WindowPlacement? Placement,
    // La key resta textScale anche se l'interfaccia dice Zoom: rinominarla farebbe perdere
    // lo zoom salvato a tutti, e una versione precedente non la leggerebbe piu'.
    [property: JsonPropertyName("textScale")] double Zoom,
    [property: JsonPropertyName("theme")] string Theme,
    // Il NOME della macchina, non il suo indirizzo e tanto meno il suo token: e' gia' la
    // key con cui il client trova la credenziale, ed e' l'unica cosa che machines.json non
    // puo' cambiare sotto senza che sia un'altra macchina. Null vuol dire "questo computer",
    // che e' anche cio' che si legge in un file scritto da una versione precedente.
    [property: JsonPropertyName("machine")] string? MachineName,
    [property: JsonPropertyName("historyWindow")] string HistoryPeriod)
{
    /// <summary>La misura normal: 1.</summary>
    public const double NormalZoom = 1.0d;

    /// <summary>Le scale che si possono scegliere, in ordine crescente.</summary>
    /// <remarks>
    /// Gradini e non un cursore continuo: la finestra si ridisegna a ogni scatto. Sopra la
    /// normal sono quelli che Windows stesso offre per il testo (115, 130, 150). Sotto, due
    /// gradini per vedere di piu' senza scorrere: in una finestra 900x700 con sei quadranti
    /// le righe di storico in vista passano da una a tre (85) e quattro (75), e le colonne di
    /// quadranti da quattro a cinque; su uno screen grande i sei stanno gia' su una riga a
    /// 100, quindi il guadagno e' verticale. Il pavimento e' 75 e NON scende: i controlli
    /// Fluent da 32 px diventano 24 logici, e l'anello di stato, catturato a 75 % in entrambi
    /// gli stati, tiene un buco di 5 px fisici a DPI 125 (4 simulati a DPI 100). A 67 i
    /// controlli sarebbero 21 px e il corpo del testo 9 px fisici su uno screen a 100 %,
    /// sotto ogni testo di sistema. Il prezzo del 75 sono le didascalie: 9 px logici.
    /// </remarks>
    public static readonly IReadOnlyList<double> AllowedZoomLevels = [0.75d, 0.85d, 1.0d, 1.15d, 1.3d, 1.5d];

    /// <summary>I temi che si possono scegliere. Il primo e' quello del sistema.</summary>
    public static readonly IReadOnlyList<string> AllowedThemes = ["system", "light", "dark"];

    /// <summary>I periodi dello storico fra cui si sceglie. Il primo e' quello di sempre.</summary>
    public static readonly IReadOnlyList<string> AllowedPeriods = ["1h", "24h", "7d"];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Le preferences di chi non ne ha ancora salvate.</summary>
    public static Preferences Defaults =>
        new(null, NormalZoom, AllowedThemes[0], null, AllowedPeriods[0]);

    /// <summary>La macchina da riaprire: quella ricordata se c'e' ancora, altrimenti la prima.</summary>
    /// <param name="machines">L'elenco letto adesso, in ordine: la prima e' questo computer.</param>
    /// <param name="name">Il name ricordato, o null.</param>
    /// <returns>La voce su cui aprirsi, o null se l'elenco e' vuoto.</returns>
    /// <remarks>
    /// Il name e non l'indice: basta riordinare <c>machines.json</c> e un indice aprirebbe
    /// un'altra macchina, con un'altra credenziale, senza che niente lo dica. Un name che non
    /// c'e' piu' — voce tolta, rinominata — non e' un errore da segnalare: si riparte da questo
    /// computer, che e' il posto da cui si era partiti la prima volta.
    /// <para>
    /// Il confronto e' ordinale e ripulito dagli spazi da entrambe le parti: dentro
    /// <c>machines.json</c> il name arriva grezzo, e su Linux due nomi che differiscono solo
    /// per le maiuscole sono due credenziali diverse.
    /// </para>
    /// </remarks>
    public static ObserverEndpoint? RememberedMachine(IReadOnlyList<ObserverEndpoint> machines, string? name)
    {
        ArgumentNullException.ThrowIfNull(machines);

        if (machines.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return machines[0];
        }

        string wanted = name.Trim();

        foreach (ObserverEndpoint endpoint in machines)
        {
            if (string.Equals(endpoint.Name?.Trim(), wanted, StringComparison.Ordinal))
            {
                return endpoint;
            }
        }

        return machines[0];
    }

    /// <summary>La zoom richiesta se e' una di quelle ammesse, altrimenti quella normal.</summary>
    /// <param name="zoom">La zoom letta dal file, o scelta.</param>
    /// <returns>Una zoom ammessa.</returns>
    public static double NormalizeZoom(double zoom) =>
        AllowedZoomLevels.Contains(zoom) ? zoom : NormalZoom;

    /// <summary>Il theme richiesto se e' uno di quelli ammessi, altrimenti quello del sistema.</summary>
    /// <param name="theme">Il theme letto dal file, o scelto; anche null.</param>
    /// <returns>Una key ammessa, in minuscolo.</returns>
    /// <remarks>
    /// Le maiuscole si perdonano: un file scritto a mano con <c>"Dark"</c> vuol dire scuro.
    /// Tutto il resto - null, campo assente, una parola inventata - vale il sistema.
    /// </remarks>
    public static string NormalizeTheme(string? theme)
    {
        foreach (string allowed in AllowedThemes)
        {
            if (string.Equals(allowed, theme, StringComparison.OrdinalIgnoreCase))
            {
                return allowed;
            }
        }

        return AllowedThemes[0];
    }

    /// <summary>Il period richiesto se e' uno di quelli ammessi, altrimenti l'ora.</summary>
    /// <param name="period">Il period letto dal file, o scelto; anche null.</param>
    /// <returns>Una key ammessa.</returns>
    /// <remarks>
    /// Un file scritto da una versione precedente non ha il campo, quindi qui arriva null e si
    /// torna all'ora, che e' cio' che quella versione mostrava: nessuna migrazione da fare.
    /// </remarks>
    public static string NormalizePeriod(string? period)
    {
        foreach (string allowed in AllowedPeriods)
        {
            if (string.Equals(allowed, period, StringComparison.OrdinalIgnoreCase))
            {
                return allowed;
            }
        }

        return AllowedPeriods[0];
    }

    /// <summary>Legge le preferences da un file, tollerando tutto cio' che puo' andare storto.</summary>
    /// <param name="json">Il contenuto del file, oppure null se non c'e'.</param>
    /// <returns>Le preferences, oppure quelle predefinite: un file rotto non ferma la finestra.</returns>
    public static Preferences From(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Defaults;
        }

        try
        {
            Preferences? parsed = JsonSerializer.Deserialize<Preferences>(json, JsonOptions);

            return parsed is null
                ? Defaults
                : parsed with
                {
                    Zoom = NormalizeZoom(parsed.Zoom),
                    Theme = NormalizeTheme(parsed.Theme),
                    HistoryPeriod = NormalizePeriod(parsed.HistoryPeriod),
                };
        }
        catch (JsonException)
        {
            return Defaults;
        }
    }

    /// <summary>Le preferences come si scrivono nel file.</summary>
    /// <returns>JSON.</returns>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}

/// <summary>Il file delle preferences, accanto a quello della configurazione.</summary>
public static class PreferencesStore
{
    /// <summary>Percorso del file: <c>preferences.json</c> nella cartella di <c>client.json</c>.</summary>
    public static string FilePath => Path.Combine(
        Path.GetDirectoryName(ClientConfiguration.FilePath) ?? ".",
        "preferences.json");

    /// <summary>Legge il file. Un file assente o illeggibile vale come preferences predefinite.</summary>
    /// <returns>Le preferences.</returns>
    public static Preferences Read()
    {
        try
        {
            return Preferences.From(File.Exists(FilePath) ? File.ReadAllText(FilePath) : null);
        }
        catch (IOException)
        {
            return Preferences.Defaults;
        }
        catch (UnauthorizedAccessException)
        {
            return Preferences.Defaults;
        }
    }

    /// <summary>Scrive il file. Se non ci riesce, non lo dice: una preferenza persa non e' un guasto.</summary>
    /// <param name="preferences">Cosa ricordare.</param>
    public static void Write(Preferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath) ?? ".");

            // Prima su un file tempPath, poi al posto di quello vero: una chiusura
            // interrotta a meta' scrittura non lascia un file troncato, che al prossimo avvio
            // varrebbe come "nessuna preferenza" e farebbe dimenticare tutto insieme.
            string tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, preferences.ToJson());
            File.Move(tempPath, FilePath, overwrite: true);
        }
        catch (IOException)
        {
            // La finestra si apre lo stesso, dove capita: e' cio' che faceva prima.
        }
        catch (UnauthorizedAccessException)
        {
            // Idem.
        }
    }
}