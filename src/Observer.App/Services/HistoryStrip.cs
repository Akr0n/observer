using System.Globalization;

namespace Observer.App.Services;

/// <summary>Che cosa si sa di un rangeText della strip.</summary>
public enum BarKind
{
    /// <summary>Nessun campione: in quell'rangeText la macchina non stava misurando.</summary>
    Missing = 0,

    /// <summary>Qualche campione, ma non tutti: coperto solo in parte.</summary>
    Partial = 1,

    /// <summary>Intervallo coperto per intero.</summary>
    Measured = 2,
}

/// <summary>Un rangeText della strip dello storico.</summary>
/// <param name="Start">L'timestamp da cui parte l'rangeText.</param>
/// <param name="Genere">Quanto se ne sa.</param>
/// <param name="Media">La media dei campioni, da 0 a 1. Zero quando non ce ne sono.</param>
/// <param name="Massimo">Il massimo raggiunto, da 0 a 1.</param>
/// <param name="Minimo">Il minimo toccato, da 0 a 1.</param>
/// <param name="Campioni">Quanti campioni sono caduti nell'rangeText.</param>
/// <param name="Attesi">Quanti ne sarebbero caduti se fosse stato coperto per intero.</param>
public sealed record HistoryBar(
    DateTimeOffset Start,
    BarKind Genere,
    double Media,
    double Massimo,
    double Minimo,
    int Campioni,
    int Attesi);

/// <summary>Un rangeText in cui una macchina non stava misurando.</summary>
/// <param name="Start">Quando ha smesso, nell'orologio di QUELLA macchina.</param>
/// <param name="End">Quando ha ripreso, nell'orologio di quella macchina.</param>
/// <param name="AtEdge">
/// True quando il vuoto tocca il bordo piu' vecchio della window esaminata, cioe' quando non
/// si sa se e' un'interruzione o semplicemente la end di cio' che il servizio conserva.
/// </param>
public sealed record HistoryGap(DateTimeOffset Start, DateTimeOffset End, bool AtEdge)
{
    /// <summary>Quanto e' durata.</summary>
    public TimeSpan Duration => End - Start;
}

/// <summary>
/// From cio' che il servizio manda a cio' che si disegna: la griglia degli intervalli.
/// </summary>
/// <remarks>
/// <b>Il servizio non manda i buchi.</b> Un rangeText in cui non e' stato campionato niente
/// semplicemente non compare nell'array dei points — non arriva con zero campioni, non arriva
/// affatto. Misurato uccidendo il servizio per 95 secondi: al livello di un minuto il bucket
/// di quel minuto non esiste, e l'array salta direttamente al successivo.
/// <para>
/// From qui la regola che questa classe esiste per far rispettare: <b>la griglia si costruisce
/// dai tempi expectedSamples, e i points ci si cercano dentro</b>, mai il contrario. Scorrere l'array e
/// disegnare una barretta per point darebbe una strip continua e piena anche su una
/// macchina spenta meta' giornata: i buchi sparirebbero stringendosi, e chi guarda leggerebbe
/// una macchina sempre accesa. E' il modo piu' facile di raccontare una bugia con dei dati
/// veri.
/// </para>
/// <para>
/// Un rangeText coperto solo in parte esiste ed e' un terzo caso: arriva con un numero di
/// campioni ridotto (misurati 53 e 31 su 60) e con una media calcolata solo su quelli. E' un
/// numero plausibile su mezzo minuto, e va detto che e' mezzo minuto.
/// </para>
/// </remarks>
public static class HistoryStrip
{
    /// <summary>Quanti campioni ci si aspetta in un rangeText, a un campione al secondo.</summary>
    /// <param name="step">La durata dell'rangeText.</param>
    /// <returns>Il numero di campioni expectedSamples, almeno uno.</returns>
    /// <remarks>
    /// Il servizio campiona a 1 Hz, quindi i campioni expectedSamples coincidono con i secondi. E'
    /// misurato: gli intervalli pieni arrivano con 60 campioni al minuto e 300 a cinque minuti.
    /// </remarks>
    public static int ExpectedSamplesIn(TimeSpan step) => Math.Max(1, (int)Math.Round(step.TotalSeconds));

    /// <summary>Costruisce la strip, buchi compresi.</summary>
    /// <param name="points">I points arrivati dal servizio, in qualsiasi ordine.</param>
    /// <param name="end">La end della window: l'ultimo rangeText e' quello che la contiene.</param>
    /// <param name="barCount">Quanti intervalli mostrare.</param>
    /// <param name="step">Quanto dura ciascun rangeText.</param>
    /// <returns>Gli intervalli dal piu' vecchio al piu' recente, uno per posizione.</returns>
    public static IReadOnlyList<HistoryBar> Build(
        IReadOnlyList<HistoryPoint> points,
        DateTimeOffset end,
        int barCount,
        TimeSpan step)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentOutOfRangeException.ThrowIfLessThan(barCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(step, TimeSpan.Zero);

        int expectedSamples = ExpectedSamplesIn(step);

        // PRIMA si raggruppa, e non e' un di piu': quando il step della bar e' piu' largo
        // di quello dei points - un quarto d'ora di bar su points da cinque minuti - nello
        // stesso rangeText ne cadono tre, e indicizzarli per timestamp ne terrebbe UNO,
        // l'ultimo iterato, buttando gli altri due. La bar mostrerebbe l'ultimo campione
        // spacciandolo per la media di tutti, e il conteggio direbbe 1 su 900. Bucket
        // ricalcola la media dalla SOMMA, che e' l'unico modo di non far pesare uguale
        // intervalli con un numero diverso di campioni.
        points = Bucket(points, step);

        // I points si indicizzano per l'start del proprio rangeText, arrotondato al step:
        // cosi' un timestamp che arriva con qualche millisecondo di scarto cade lo stesso
        // nella casella giusta invece di sparire.
        Dictionary<DateTimeOffset, HistoryPoint> byStart = [];

        foreach (HistoryPoint point in points)
        {
            byStart[AlignTo(point.Timestamp, step)] = point;
        }

        DateTimeOffset ultimo = AlignTo(end, step);
        List<HistoryBar> strip = new(barCount);

        for (int i = barCount - 1; i >= 0; i--)
        {
            DateTimeOffset start = ultimo - (step * i);

            strip.Add(byStart.TryGetValue(start, out HistoryPoint? point)
                ? From(point, start, expectedSamples)
                : new HistoryBar(start, BarKind.Missing, 0d, 0d, 0d, 0, expectedSamples));
        }

        return strip;
    }

    /// <summary>Quando quella macchina NON stava misurando, nella window data.</summary>
    /// <param name="points">I points arrivati dal servizio di quella macchina.</param>
    /// <param name="window">Quanto indietro guardare.</param>
    /// <param name="step">La risoluzione con cui cercare i vuoti.</param>
    /// <returns>I vuoti dal piu' vecchio al piu' recente, vuoto se non ce ne sono.</returns>
    /// <remarks>
    /// <para>
    /// Poggia sull'invariante che <see cref="Build"/> esiste per far rispettare — <b>il
    /// servizio non manda i buchi</b>, quindi la griglia si costruisce dai tempi expectedSamples e i
    /// points ci si cercano dentro. Qui non si disegna: si contano le caselle rimaste vuote.
    /// </para>
    /// <para>
    /// <b>Tutto sta nell'orologio della MACCHINA, mai in quello del client.</b> La window si
    /// ancora al point piu' recente che quella macchina ha mandato, non a "adesso" di chi
    /// guarda. E' la differenza fra un riepilogo e un generatore di falsi allarmi: due orologi
    /// che divergono di venti minuti - una macchina virtuale, un Windows fuori dominio - non
    /// producono nessun vuoto, perche' uno scarto trasla l'intera serie e non apre buchi in
    /// mezzo. E il ritardo del consolidamento si esclude da se': dopo l'ultimo point non c'e'
    /// nessuna casella da riempire, quindi non si segnala mai un vuoto che tocca l'adesso.
    /// </para>
    /// <para>
    /// Il step e' quello della SORGENTE e non quello della bar della strip, ed e' la cosa
    /// che si sbaglia per prima: a sette giorni una bar copre due ore, e un'interruzione di
    /// quaranta minuti ci finisce dentro come rangeText <i>parziale</i>, cioe' non verrebbe
    /// vista affatto.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<HistoryGap> FindGaps(
        IReadOnlyList<HistoryPoint> points,
        TimeSpan window,
        TimeSpan step)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(step, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, step);

        if (points.Count == 0)
        {
            // Nessun point non vuol dire "sempre assente": vuol dire che non si sa niente, e il
            // chiamante lo dice con un'altra frase. Restituire un vuoto lungo quanto la
            // window sarebbe inventare un'interruzione mai osservata.
            return [];
        }

        DateTimeOffset ultimo = points[0].Timestamp;

        foreach (HistoryPoint point in points)
        {
            if (point.Timestamp > ultimo)
            {
                ultimo = point.Timestamp;
            }
        }

        IReadOnlyList<HistoryBar> bars = Build(points, ultimo, (int)(window / step), step);
        List<HistoryGap> gaps = [];
        int gapStartIndex = -1;

        for (int i = 0; i < bars.Count; i++)
        {
            if (bars[i].Genere == BarKind.Missing)
            {
                if (gapStartIndex < 0)
                {
                    gapStartIndex = i;
                }

                continue;
            }

            if (gapStartIndex >= 0)
            {
                gaps.Add(new HistoryGap(bars[gapStartIndex].Start, bars[i].Start, AtEdge: gapStartIndex == 0));
                gapStartIndex = -1;
            }
        }

        // Una corsa che arriva in fondo non puo' esistere: l'ultima bar contiene per
        // costruzione il point piu' recente, quindi e' misurata. Se un giorno l'ancoraggio
        // cambiasse, questa riga la chiuderebbe lo stesso invece di perderla in silenzio.
        if (gapStartIndex >= 0)
        {
            gaps.Add(new HistoryGap(bars[gapStartIndex].Start, bars[^1].Start + step, AtEdge: gapStartIndex == 0));
        }

        return gaps;
    }

    /// <summary>Quanto e' larga davvero una bar, in proporzione a quanto ha coperto.</summary>
    /// <param name="bar">La bar da disegnare.</param>
    /// <param name="width">La width piena della colonna.</param>
    /// <returns>La width da disegnare, mai sotto un pixel.</returns>
    /// <remarks>
    /// Serve per l'ULTIMA bar della strip, che e' sempre l'rangeText <b>in corso</b>: a
    /// step di un minuto contiene fra zero e sessanta secondi di misure e la differenza non
    /// si nota, ma a step di due ore puo' contenerne cinque minuti e disegnarsi identica a
    /// una bar piena — proprio bucketStart l'occhio legge "adesso". Una bar che ha coperto un
    /// dodicesimo del suo rangeText si disegna larga un dodicesimo.
    /// <para>
    /// Quanto la si veda crescere dipende da chi rilegge, non da qui, ed e' il motivo per cui
    /// <c>MainViewModel.ProssimaLettura</c> non rilegge ogni step: rileggendo al step si
    /// guarderebbe ogni volta una bar appena nata, sempre alla stessa frazione, e l'estremo
    /// destro della strip resterebbe congelato per tutta la sessione a quella width li'.
    /// </para>
    /// <para>
    /// Vale per ogni bar parziale, non solo per l'ultima: anche a meta' strip, un
    /// rangeText coperto a meta' sa meno di uno coperto per intero, e la width lo dice
    /// senza bisogno di un terzo colore. Le bars intere e i buchi non si toccano: un buco ha
    /// existing' il suo segno, e stringere una bar piena sarebbe una bugia al contrario.
    /// </para>
    /// </remarks>
    public static double WidthOf(HistoryBar bar, double width)
    {
        ArgumentNullException.ThrowIfNull(bar);

        if (bar.Genere != BarKind.Partial || bar.Attesi <= 0)
        {
            return width;
        }

        double coverage = Math.Clamp((double)bar.Campioni / bar.Attesi, 0d, 1d);

        // Almeno un pixel: una bar che esiste non deve sparire del tutto, o si leggerebbe
        // come un buco, che vuol dire un'altra cosa.
        return Math.Max(1d, width * coverage);
    }

    /// <summary>Bucket campioni fitti in intervalli piu' larghi.</summary>
    /// <param name="points">I points da raggruppare.</param>
    /// <param name="step">La durata dell'rangeText di destinazione.</param>
    /// <returns>Un point per rangeText che contiene almeno un campione.</returns>
    /// <remarks>
    /// Serve per la tail della strip, che si legge dai campioni grezzi. <b>La media si
    /// ricalcola dalla somma, non come media delle medie</b>: intervalli con un numero diverso
    /// di campioni peserebbero uguale, e ne uscirebbe un numero credibile e falso.
    /// </remarks>
    public static IReadOnlyList<HistoryPoint> Bucket(
        IReadOnlyList<HistoryPoint> points,
        TimeSpan step)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(step, TimeSpan.Zero);

        Dictionary<DateTimeOffset, (double Sum, double Min, double Max, int Count)> buckets = [];

        foreach (HistoryPoint point in points)
        {
            DateTimeOffset bucketStart = AlignTo(point.Timestamp, step);
            int sampleCount = Math.Max(1, point.Count);

            if (buckets.TryGetValue(bucketStart, out (double Sum, double Min, double Max, int Count) existing))
            {
                buckets[bucketStart] = (
                    existing.Sum + (point.Avg * sampleCount),
                    Math.Min(existing.Min, point.Min),
                    Math.Max(existing.Max, point.Max),
                    existing.Count + sampleCount);
            }
            else
            {
                buckets[bucketStart] = (point.Avg * sampleCount, point.Min, point.Max, sampleCount);
            }
        }

        return [.. buckets
            .OrderBy(bucket => bucket.Key)
            .Select(bucket => new HistoryPoint(
                bucket.Key,
                bucket.Value.Count,
                bucket.Value.Sum / bucket.Value.Count,
                bucket.Value.Min,
                bucket.Value.Max,
                bucket.Value.Sum / bucket.Value.Count))];
    }

    /// <summary>Unisce due letture della stessa serie, tenendo la piu' fresca bucketStart si sovrappongono.</summary>
    /// <param name="aggregates">La lettura che copre tutta la window, ma e' indietro.</param>
    /// <param name="tail">La lettura fresca degli ultimi intervalli.</param>
    /// <returns>I points merged.</returns>
    /// <remarks>
    /// La strip si costruisce con DUE letture, e il motivo e' misurato: il consolidamento
    /// degli aggregati ha una grazia di quattro minuti, quindi il livello a un minuto e'
    /// indietro di cinque o sei minuti rispetto ad adesso. Con la sola lettura aggregata le
    /// ultime barrette sarebbero <b>sempre</b> vuote, e la strip direbbe "non misurato"
    /// proprio sull'adesso — mentre i quadranti sopra mostrano valori vivi. La tail arriva dal
    /// grezzo, che e' aggiornato al secondo.
    /// </remarks>
    public static IReadOnlyList<HistoryPoint> Merge(
        IReadOnlyList<HistoryPoint> aggregates,
        IReadOnlyList<HistoryPoint> tail)
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        ArgumentNullException.ThrowIfNull(tail);

        Dictionary<DateTimeOffset, HistoryPoint> merged = [];

        foreach (HistoryPoint point in aggregates)
        {
            merged[point.Timestamp] = point;
        }

        // Dove le due si sovrappongono vince quella con PIU' campioni, non la piu' fresca.
        // Quasi sempre e' la tail, ed e' il motivo per cui questa funzione esiste: su un
        // rangeText consolidato a meta' l'aggregates ha meno campioni e mentirebbe. Ma c'e'
        // un rangeText in cui perde, ed e' sempre lo stesso: il piu' VECCHIO della tail. Il
        // grezzo si chiede da un timestamp qualsiasi - "dieci minuti fa" - che non cade sul
        // confine di un rangeText, quindi quel primo rangeText arriva tagliato, con trenta
        // campioni su sessanta, mentre l'aggregates ce li ha tutti. Lasciandolo vincere, una
        // bar misurata per intero si disegnava larga la meta' (e' parziale), il suggerimento
        // diceva "30 of 60 samples", e media, minimo e massimo saltavano la prima meta' del
        // minuto: un picco li' dentro spariva. Il confine si sposta a ogni lettura, quindi la
        // bar sbagliata era sempre la stessa posizione della strip.
        foreach (HistoryPoint point in tail)
        {
            merged[point.Timestamp] =
                merged.TryGetValue(point.Timestamp, out HistoryPoint? existing) && existing.Count > point.Count
                    ? existing
                    : point;
        }

        return [.. merged.Values.OrderBy(point => point.Timestamp)];
    }

    private static HistoryBar From(HistoryPoint point, DateTimeOffset start, int expectedSamples) =>
        new(
            start,
            point.Count >= expectedSamples ? BarKind.Measured : BarKind.Partial,
            point.Avg,
            point.Max,
            point.Min,
            point.Count,
            expectedSamples);

    private static DateTimeOffset AlignTo(DateTimeOffset timestamp, TimeSpan step) =>
        new(timestamp.UtcTicks - (timestamp.UtcTicks % step.Ticks), TimeSpan.Zero);

    /// <summary>Quale bar sta sotto una certa ascissa.</summary>
    /// <param name="x">Ascissa del puntatore, in pixel dal bordo sinistro della strip.</param>
    /// <param name="width">Larghezza dell'intera strip.</param>
    /// <param name="barCount">Quante bars ci sono.</param>
    /// <returns>L'index, oppure -1 se il puntatore e' fuori.</returns>
    /// <remarks>
    /// Sta qui e non nel controllo per la stessa ragione dell'aritmetica dell'arco: un errore
    /// di un index non fa fallire niente, mostra soltanto l'ora della bar accanto. E il
    /// bordo destro sbaglia da solo — con x uguale alla width la divisione da'
    /// esattamente <c>barCount</c>, cioe' un index che non esiste.
    /// </remarks>
    public static int IndexAt(double x, double width, int barCount)
    {
        if (barCount <= 0 || width <= 0d || double.IsNaN(x) || x < 0d || x >= width)
        {
            return -1;
        }

        return Math.Clamp((int)(x / (width / barCount)), 0, barCount - 1);
    }

    /// <summary>Che cosa dire di una bar a chi ci passa sopra il mouse.</summary>
    /// <param name="bars">Le bars della strip.</param>
    /// <param name="index">Quale bar.</param>
    /// <returns>La frase da mostrare, vuota se l'index non esiste.</returns>
    /// <remarks>
    /// Dice l'INTERVALLO, non l'timestamp: una bar copre da un minuto a due ore secondo il
    /// periodo scelto, e mostrarne solo l'start lascerebbe indovinare quanto e' larga. Il
    /// step si ricava dalle bars stesse invece di essere una costante, cosi' resta vero
    /// qualunque periodo la strip stia mostrando.
    /// <para>
    /// Su una bar vuota lo dice: "non misurato" non e' "zero", ed e' la stessa distinzione
    /// che il disegno existing' fa con il tratteggio.
    /// </para>
    /// </remarks>
    public static string Describe(IReadOnlyList<HistoryBar> bars, int index)
    {
        ArgumentNullException.ThrowIfNull(bars);

        if (index < 0 || index >= bars.Count)
        {
            return string.Empty;
        }

        HistoryBar bar = bars[index];

        if (bars.Count < 2)
        {
            return FormatTime(bar.Start, includeDay: false);
        }

        TimeSpan step = bars[1].Start - bars[0].Start;

        // Oltre le ventiquattro ore l'ora da sola non colloca piu' niente: a sette giorni la
        // stessa frase - "04:00 – 06:00" - compare su SETTE bars, una per giorno, e chi vede
        // un picco (che e' il motivo per cui si guarda una settimana) non ha modo di sapere di
        // che giorno sia. Il nome del giorno basta, la data no: fra due bars della stessa
        // strip passano al massimo 83 x 2 h = 166 ore, meno di una settimana, quindi la
        // coppia (giorno, ora) non puo' ripetersi. La soglia e' stretta di proposito: a
        // ventiquattro ore l'arco vale esattamente un giorno, gli estremi non si toccano, e la
        // frase resta corta bucketStart non serve allungarla.
        bool includeDay = (step * bars.Count) > TimeSpan.FromHours(24);

        string rangeText = FormatTime(bar.Start, includeDay) + " – " + FormatTime(bar.Start + step, includeDay);

        return bar.Genere switch
        {
            BarKind.Missing => rangeText + " · not measured",
            BarKind.Partial => rangeText + $" · {bar.Campioni} of {bar.Attesi} samples",
            _ => rangeText,
        };
    }

    private static string FormatTime(DateTimeOffset timestamp, bool includeDay) =>
        timestamp.ToLocalTime().ToString(includeDay ? "ddd HH:mm" : "HH:mm", CultureInfo.InvariantCulture);
}