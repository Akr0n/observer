using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Observer.App.Services;

/// <summary>Dove stava la finestra l'ultima volta.</summary>
/// <param name="X">Bordo sinistro, in pixel fisici dello schermo.</param>
/// <param name="Y">Bordo superiore, in pixel fisici dello schermo.</param>
/// <param name="Width">Larghezza, in pixel logici (quelli con cui la finestra si misura).</param>
/// <param name="Height">Altezza, in pixel logici.</param>
/// <param name="Maximized">True se era a tutto schermo: allora X, Y e le misure sono quelle di prima.</param>
/// <remarks>
/// Posizione fisica e misure logiche, e non e' un'incoerenza: e' come Avalonia le espone
/// (<c>Position</c> e' un <c>PixelPoint</c>, <c>Width</c> e' in unita' indipendenti dal DPI),
/// e convertire da una parte all'altra con la scala dello schermo di ieri darebbe una finestra
/// di misura diversa il giorno in cui la scala cambia.
/// </remarks>
public sealed record PosizioneFinestra(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("maximized")] bool Maximized)
{
    /// <summary>Quanto della finestra deve stare dentro uno schermo perche' la si possa afferrare.</summary>
    /// <remarks>
    /// Un quadrato di 120 pixel fisici a partire dall'angolo in alto a sinistra: ci sta dentro
    /// l'icona e un pezzo di barra del titolo, cioe' il minimo per trascinarla via se il resto
    /// e' fuori. Il caso che questo evita e' un monitor scollegato: senza controllo la finestra
    /// riaprirebbe su uno schermo che non c'e' piu', invisibile e senza modo di raggiungerla.
    /// </remarks>
    public const int MinimoAfferrabile = 120;

    /// <summary>Di quanto il bordo sinistro puo' sporgere fuori dallo schermo.</summary>
    /// <remarks>
    /// Windows disegna attorno a ogni finestra un bordo invisibile di 7-8 pixel, e una
    /// finestra agganciata al bordo sinistro (Win+Freccia) sta a X = -8: senza questa
    /// tolleranza non verrebbe mai ricordata. In alto no: il bordo invisibile in alto non
    /// c'e', e una finestra massimizzata sta a (-8, -8), che cosi' resta esclusa.
    /// </remarks>
    public const int Tolleranza = 16;

    /// <summary>Una posizione che porta solo lo stato: non passa <see cref="SuUnoDegli"/>.</summary>
    private static PosizioneFinestra SoloStato => new(0, 0, 0, 0, Maximized: false);

    /// <summary>Un'area di lavoro, in pixel fisici.</summary>
    /// <param name="X">Bordo sinistro.</param>
    /// <param name="Y">Bordo superiore.</param>
    /// <param name="Width">Larghezza.</param>
    /// <param name="Height">Altezza.</param>
    public readonly record struct AreaDiLavoro(int X, int Y, int Width, int Height);

    /// <summary>Questa posizione, se sta su uno degli schermi di adesso; altrimenti null.</summary>
    /// <param name="schermi">Le aree di lavoro degli schermi collegati.</param>
    /// <returns>Se stessa, oppure null quando la finestra riaprirebbe fuori da tutto.</returns>
    public PosizioneFinestra? SuUnoDegli(IReadOnlyList<AreaDiLavoro> schermi)
    {
        ArgumentNullException.ThrowIfNull(schermi);

        if (Width < MinimoAfferrabile || Height < MinimoAfferrabile)
        {
            return null;
        }

        foreach (AreaDiLavoro schermo in schermi)
        {
            // Le costanti si sommano e sottraggono dal lato dello schermo, MAI da X o Y:
            // con un file scritto a mano che dice x = 2147483647 la somma traboccava, il
            // confronto passava, e la finestra si apriva invisibile - e si risalvava
            // identica a ogni chiusura.
            if (X >= schermo.X - Tolleranza
                && Y >= schermo.Y
                && X <= schermo.X + schermo.Width - MinimoAfferrabile
                && Y <= schermo.Y + schermo.Height - MinimoAfferrabile)
            {
                return this;
            }
        }

        return null;
    }

    /// <summary>Cosa ricordare alla chiusura, a seconda di com'e' la finestra.</summary>
    /// <param name="ridottaAIcona">True se la finestra e' ridotta a icona.</param>
    /// <param name="massimizzata">
    /// True se e' a tutto schermo, oppure se lo era prima di essere ridotta a icona.
    /// </param>
    /// <param name="ultimaNormale">
    /// L'ultima geometria vista in stato normale durante questa sessione, se c'e' stata.
    /// </param>
    /// <param name="salvata">La geometria letta dal file all'avvio, se c'era.</param>
    /// <param name="attuale">La geometria di adesso, che vale solo a finestra normale.</param>
    /// <returns>La posizione da scrivere, oppure null se non c'e' niente di sensato da dire.</returns>
    /// <remarks>
    /// Le misure di una finestra a tutto schermo sono quelle dello schermo, e la posizione di
    /// una ridotta a icona e' fuori da ogni schermo: in quei due stati si ricorda l'ultima
    /// geometria normale di QUESTA sessione, non quella letta dal file all'avvio - che e'
    /// cio' che si faceva prima, e uno spostamento fatto prima di massimizzare andava
    /// perso: su due monitor la finestra riapriva su quello sbagliato. Se nessuna geometria
    /// normale e' nota, lo stato a tutto schermo si ricorda da solo, con una posizione che
    /// <see cref="SuUnoDegli"/> scarta: la finestra si apre dove decide il sistema, ma piena.
    /// </remarks>
    public static PosizioneFinestra? AllaChiusura(
        bool ridottaAIcona,
        bool massimizzata,
        PosizioneFinestra? ultimaNormale,
        PosizioneFinestra? salvata,
        PosizioneFinestra attuale)
    {
        ArgumentNullException.ThrowIfNull(attuale);

        if (!ridottaAIcona && !massimizzata)
        {
            return attuale with { Maximized = false };
        }

        PosizioneFinestra? normale = ultimaNormale ?? salvata;

        if (massimizzata)
        {
            return (normale ?? SoloStato) with { Maximized = true };
        }

        return normale is null ? null : normale with { Maximized = false };
    }
}

/// <summary>Una voce del selettore della misura del testo.</summary>
/// <param name="Fattore">La scala: 1 e' la misura normale.</param>
/// <remarks>
/// Il testo della voce E' il suo <see cref="ToString"/>: un lettore di schermo annuncia
/// quello, e con un double nudo annunciava "1,15" al posto di "115 %". L'uguaglianza per
/// valore del record e' cio' che fa ritrovare la voce a partire dal numero.
/// </remarks>
public sealed record OpzioneScala(double Fattore)
{
    /// <inheritdoc />
    public override string ToString() => Fattore.ToString("P0", CultureInfo.CurrentCulture);
}

/// <summary>Cio' che la dashboard ricorda di se' fra un avvio e l'altro.</summary>
/// <param name="Finestra">Dove stava la finestra, oppure null se non lo sa ancora.</param>
/// <param name="ScalaTesto">Quanto e' ingrandita la finestra: 1 e' la misura normale.</param>
/// <remarks>
/// Un file a parte e non <c>client.json</c>: quello porta una credenziale, e un programma che lo
/// riscrivesse a ogni chiusura per salvare due numeri sarebbe un programma che riscrive una
/// credenziale a ogni chiusura.
/// </remarks>
public sealed record Preferenze(
    [property: JsonPropertyName("window")] PosizioneFinestra? Finestra,
    [property: JsonPropertyName("textScale")] double ScalaTesto)
{
    /// <summary>Le scale che si possono scegliere. La prima e' la misura normale.</summary>
    /// <remarks>
    /// Quattro gradini e non un cursore continuo: la finestra si ridisegna a ogni scatto, e i
    /// gradini sono quelli che Windows stesso offre per il testo (100, 115, 130, 150).
    /// </remarks>
    public static readonly IReadOnlyList<double> ScaleAmmesse = [1.0d, 1.15d, 1.3d, 1.5d];

    private static readonly JsonSerializerOptions Opzioni = new(JsonSerializerDefaults.Web);

    /// <summary>Le preferenze di chi non ne ha ancora salvate.</summary>
    public static Preferenze Predefinite => new(null, ScaleAmmesse[0]);

    /// <summary>La scala richiesta se e' una di quelle ammesse, altrimenti quella normale.</summary>
    /// <param name="scala">La scala letta dal file, o scelta.</param>
    /// <returns>Una scala ammessa.</returns>
    public static double ScalaValida(double scala) =>
        ScaleAmmesse.Contains(scala) ? scala : ScaleAmmesse[0];

    /// <summary>Legge le preferenze da un file, tollerando tutto cio' che puo' andare storto.</summary>
    /// <param name="json">Il contenuto del file, oppure null se non c'e'.</param>
    /// <returns>Le preferenze, oppure quelle predefinite: un file rotto non ferma la finestra.</returns>
    public static Preferenze Da(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Predefinite;
        }

        try
        {
            Preferenze? lette = JsonSerializer.Deserialize<Preferenze>(json, Opzioni);

            return lette is null
                ? Predefinite
                : lette with { ScalaTesto = ScalaValida(lette.ScalaTesto) };
        }
        catch (JsonException)
        {
            return Predefinite;
        }
    }

    /// <summary>Le preferenze come si scrivono nel file.</summary>
    /// <returns>JSON.</returns>
    public string InJson() => JsonSerializer.Serialize(this, Opzioni);
}

/// <summary>Il file delle preferenze, accanto a quello della configurazione.</summary>
public static class PreferenzeStore
{
    /// <summary>Percorso del file: <c>preferences.json</c> nella cartella di <c>client.json</c>.</summary>
    public static string FilePath => Path.Combine(
        Path.GetDirectoryName(ClientConfiguration.FilePath) ?? ".",
        "preferences.json");

    /// <summary>Legge il file. Un file assente o illeggibile vale come preferenze predefinite.</summary>
    /// <returns>Le preferenze.</returns>
    public static Preferenze Leggi()
    {
        try
        {
            return Preferenze.Da(File.Exists(FilePath) ? File.ReadAllText(FilePath) : null);
        }
        catch (IOException)
        {
            return Preferenze.Predefinite;
        }
        catch (UnauthorizedAccessException)
        {
            return Preferenze.Predefinite;
        }
    }

    /// <summary>Scrive il file. Se non ci riesce, non lo dice: una preferenza persa non e' un guasto.</summary>
    /// <param name="preferenze">Cosa ricordare.</param>
    public static void Scrivi(Preferenze preferenze)
    {
        ArgumentNullException.ThrowIfNull(preferenze);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath) ?? ".");
            File.WriteAllText(FilePath, preferenze.InJson());
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