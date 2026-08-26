using Observer.Core.Metrics;

namespace Observer.App.Services;

/// <summary>
/// Quanto e' grave cio' che una riga o un gruppo sta dicendo. Governa solo il colore.
/// </summary>
public enum MetricSeverity
{
    /// <summary>Valore valido.</summary>
    Ok = 0,

    /// <summary>In avvio: manca il secondo campione. Normale, non un guasto.</summary>
    InAttesa = 1,

    /// <summary>Non misurabile su questa piattaforma. E' un'informazione, non un errore.</summary>
    NonMisurabile = 2,

    /// <summary>Doveva esserci un valore e non c'e'.</summary>
    Problema = 3,
}

/// <summary>
/// Una riga della schermata.
/// </summary>
/// <param name="Key">Identita' stabile della riga, per aggiornarla senza ricrearla.</param>
/// <param name="Label">Nome leggibile, con l'istanza fra parentesi quando c'e'.</param>
/// <param name="Display">Il valore formattato, oppure il motivo per cui manca.</param>
/// <param name="Fraction">Frazione 0..1 per la barra, null quando non e' una percentuale.</param>
/// <param name="Severity">Gravita' di cio' che la riga sta dicendo.</param>
public sealed record MetricRowState(
    string Key,
    string Label,
    string Display,
    double? Fraction,
    MetricSeverity Severity);

/// <summary>
/// Un riquadro della schermata: un collector con le sue righe.
/// </summary>
/// <param name="CollectorId">Identificatore del collector.</param>
/// <param name="Title">Titolo leggibile del riquadro.</param>
/// <param name="Note">
/// Motivo per cui il collector e' degradato, oppure null. E' cio' che riempie il riquadro
/// quando <paramref name="Rows"/> e' vuoto, perche' un riquadro vuoto non si diagnostica.
/// </param>
/// <param name="Severity">Gravita' dello stato del collector.</param>
/// <param name="Rows">Le righe misurate.</param>
public sealed record MetricGroupState(
    string CollectorId,
    string Title,
    string? Note,
    MetricSeverity Severity,
    IReadOnlyList<MetricRowState> Rows);

/// <summary>
/// Traduce un campionamento nelle righe da disegnare.
/// </summary>
/// <remarks>
/// E' una funzione pura: campionamento e catalogo entrano, righe escono. E' il pezzo
/// dell'applicazione che si puo' verificare con dei test invece che a occhio, ed e' anche
/// quello dove i difetti sono silenziosi — uno stato degradato tradotto in uno zero
/// somiglia troppo a una misura vera.
/// </remarks>
public static class SnapshotProjection
{
    // Il servizio non dichiara un nome leggibile per il COLLECTOR, solo per le metriche.
    // Questa tabellina serve a non intitolare un riquadro "memory": chi non programma legge
    // "Memoria". Un collector sconosciuto tiene il proprio identificatore, quindi
    // aggiungerne uno nuovo al servizio non richiede di toccare questo file.
    private static readonly Dictionary<string, string> TitoliNoti = new(StringComparer.Ordinal)
    {
        ["cpu"] = "CPU",
        ["memory"] = "Memoria",
    };

    /// <summary>Costruisce i riquadri da mostrare.</summary>
    /// <param name="snapshot">L'ultimo campionamento ricevuto.</param>
    /// <param name="catalog">Il catalogo, oppure <see cref="MetricCatalog.Empty"/>.</param>
    public static IReadOnlyList<MetricGroupState> Project(MachineSnapshot snapshot, MetricCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(catalog);

        List<MetricGroupState> gruppi = new(snapshot.Collectors?.Count ?? 0);

        foreach (MetricSnapshot collector in snapshot.Collectors ?? [])
        {
            if (collector is null)
            {
                continue;
            }

            List<MetricRowState> righe = [];

            foreach (MetricPoint punto in collector.Points ?? [])
            {
                if (punto is not null)
                {
                    righe.Add(Riga(collector.CollectorId, punto, catalog));
                }
            }

            Disambigua(righe, catalog);

            gruppi.Add(new MetricGroupState(
                collector.CollectorId,
                Titolo(collector.CollectorId),
                Nota(collector),
                Gravita(collector.Status),
                righe));
        }

        return gruppi;
    }

    /// <summary>
    /// Aggiunge l'unita' fra parentesi alle righe che, dentro lo stesso riquadro, finirebbero
    /// con lo stesso nome.
    /// </summary>
    /// <remarks>
    /// Serve davvero: il collector della memoria dichiara "Memoria usata" sia per i byte sia
    /// per la percentuale, e due righe con lo stesso nome e numeri diversi sembrano una
    /// contraddizione. La regola e' generica, quindi vale anche per un collector futuro che
    /// commetta lo stesso battesimo doppio.
    /// </remarks>
    private static void Disambigua(List<MetricRowState> righe, MetricCatalog catalog)
    {
        Dictionary<string, int> quante = new(StringComparer.Ordinal);

        foreach (MetricRowState riga in righe)
        {
            quante[riga.Label] = quante.TryGetValue(riga.Label, out int n) ? n + 1 : 1;
        }

        for (int i = 0; i < righe.Count; i++)
        {
            if (quante[righe[i].Label] < 2)
            {
                continue;
            }

            // La chiave contiene collectorId|metricId|istanza: il pezzo centrale e' cio' che
            // serve per ritrovare il descrittore e quindi l'unita'.
            string[] pezzi = righe[i].Key.Split('|');
            string simbolo = pezzi.Length > 1 ? catalog.Find(pezzi[1])?.Unit.Symbol ?? string.Empty : string.Empty;
            string distinzione = string.IsNullOrEmpty(simbolo) ? pezzi.ElementAtOrDefault(1) ?? "?" : simbolo;

            righe[i] = righe[i] with { Label = righe[i].Label + " (" + distinzione + ")" };
        }
    }

    private static string Titolo(string collectorId) =>
        collectorId is not null && TitoliNoti.TryGetValue(collectorId, out string? titolo)
            ? titolo
            : collectorId ?? "sorgente senza nome";

    private static string? Nota(MetricSnapshot collector)
    {
        if (collector.Status == CollectorStatus.Ok)
        {
            // Un collector Ok che non ha prodotto nulla non e' un caso normale: senza questa
            // riga il riquadro resterebbe vuoto e muto.
            return collector.Points is null || collector.Points.Count == 0
                ? "Il servizio dichiara questa sorgente funzionante ma non ha inviato alcun valore."
                : null;
        }

        return collector.Message ?? "Il servizio non ha spiegato perche' questa sorgente non ha prodotto valori.";
    }

    private static MetricRowState Riga(string collectorId, MetricPoint punto, MetricCatalog catalog)
    {
        MetricDescriptor? descrittore = catalog.Find(punto.MetricId);
        MetricUnit? unita = descrittore?.Unit;

        string etichetta = descrittore?.DisplayName ?? punto.MetricId;

        if (!string.IsNullOrWhiteSpace(punto.Instance))
        {
            etichetta = etichetta + " (" + punto.Instance + ")";
        }

        string chiave = collectorId + "|" + punto.MetricId + "|" + (punto.Instance ?? string.Empty);

        if (punto.Status != CollectorStatus.Ok)
        {
            return new MetricRowState(
                chiave,
                etichetta,
                punto.Message ?? "valore non disponibile, senza spiegazione",
                null,
                Gravita(punto.Status));
        }

        if (punto.Value is not MetricValue valore)
        {
            // Ok senza valore e' esattamente il caso che il commento in MetricPoint teme:
            // mostrare zero qui darebbe una macchina piena di zeri marcati "Ok".
            return new MetricRowState(
                chiave,
                etichetta,
                "il servizio ha dichiarato la misura riuscita ma non ha inviato il valore",
                null,
                MetricSeverity.Problema);
        }

        return new MetricRowState(
            chiave,
            etichetta,
            MetricFormatting.Describe(valore, unita),
            MetricFormatting.Fraction(valore, unita),
            valore.Kind == MetricValueKind.Unknown ? MetricSeverity.Problema : MetricSeverity.Ok);
    }

    private static MetricSeverity Gravita(CollectorStatus stato) => stato switch
    {
        CollectorStatus.Ok => MetricSeverity.Ok,
        CollectorStatus.Warmup => MetricSeverity.InAttesa,
        CollectorStatus.Unsupported => MetricSeverity.NonMisurabile,
        _ => MetricSeverity.Problema,
    };
}
