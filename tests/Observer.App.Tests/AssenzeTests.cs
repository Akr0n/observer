using Observer.App.Services;

namespace Observer.App.Tests;

/// <summary>
/// Quando una macchina NON stava misurando, letto dal suo storico al rientro.
/// </summary>
/// <remarks>
/// E' la risposta alla domanda "cosa mi sono perso mentre la finestra era chiusa", e il dato per
/// darla esiste gia' sul disco della macchina remota. Le regole qui servono tutte a una cosa
/// sola: non dichiarare un'interruzione che nessuno ha osservato. Un riepilogo che grida al lupo
/// a ogni apertura si impara a chiudere senza leggerlo, che e' peggio di non averlo.
/// </remarks>
public class AssenzeTests
{
    private static readonly DateTimeOffset Mezzogiorno = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Minuto = TimeSpan.FromMinutes(1);

    /// <summary>Punti a un passo l'uno dall'altro, da <paramref name="da"/>.</summary>
    private static IEnumerable<HistoryPoint> Serie(DateTimeOffset da, int quanti, TimeSpan? passo = null) =>
        Enumerable.Range(0, quanti)
            .Select(i => new HistoryPoint(da + ((passo ?? Minuto) * i), 60, 0.5d, 0.5d, 0.5d, 0.5d));

    [Fact]
    public void UnaMacchinaCheHaSempreMisuratoNonHaNienteDaDire()
    {
        IReadOnlyList<Assenza> assenze = HistoryStrip.Assenze(
            [.. Serie(Mezzogiorno, 30)],
            TimeSpan.FromMinutes(30),
            Minuto);

        Assert.Empty(assenze);
    }

    [Fact]
    public void UnInterruzioneInMezzoSiVedeConIDueEstremi()
    {
        // Dieci minuti di misure, venti di niente, dieci di misure: e' il caso per cui il
        // riepilogo esiste.
        List<HistoryPoint> punti = [.. Serie(Mezzogiorno, 10), .. Serie(Mezzogiorno + TimeSpan.FromMinutes(30), 10)];

        Assenza assenza = Assert.Single(HistoryStrip.Assenze(punti, TimeSpan.FromMinutes(40), Minuto));

        Assert.Equal(Mezzogiorno + TimeSpan.FromMinutes(10), assenza.Inizio);
        Assert.Equal(Mezzogiorno + TimeSpan.FromMinutes(30), assenza.Fine);
        Assert.Equal(TimeSpan.FromMinutes(20), assenza.Durata);
        Assert.False(assenza.DalBordo);
    }

    [Fact]
    public void DueInterruzioniRestanoDue()
    {
        List<HistoryPoint> punti =
        [
            .. Serie(Mezzogiorno, 5),
            .. Serie(Mezzogiorno + TimeSpan.FromMinutes(10), 5),
            .. Serie(Mezzogiorno + TimeSpan.FromMinutes(25), 5),
        ];

        IReadOnlyList<Assenza> assenze = HistoryStrip.Assenze(punti, TimeSpan.FromMinutes(30), Minuto);

        Assert.Equal(2, assenze.Count);
        Assert.Equal(TimeSpan.FromMinutes(5), assenze[0].Durata);
        Assert.Equal(TimeSpan.FromMinutes(10), assenze[1].Durata);

        // In ordine, dalla piu' vecchia: e' l'ordine in cui si racconta una giornata.
        Assert.True(assenze[0].Inizio < assenze[1].Inizio);
    }

    [Fact]
    public void IlVuotoCheToccaIlBordoVecchioSiDichiaraTale()
    {
        // A sinistra non si sa se e' un'interruzione o la fine di cio' che il servizio
        // conserva: la ritenzione cancella un PREFISSO, ed e' indistinguibile da una macchina
        // accesa a meta' finestra. Chiamarla "interruzione di 40 minuti" sarebbe inventare.
        IReadOnlyList<Assenza> assenze = HistoryStrip.Assenze(
            [.. Serie(Mezzogiorno + TimeSpan.FromMinutes(40), 20)],
            TimeSpan.FromHours(1),
            Minuto);

        Assert.True(Assert.Single(assenze).DalBordo);
    }

    [Fact]
    public void NonSiSegnalaMaiUnVuotoCheToccaLAdesso()
    {
        // Il livello aggregato e' indietro rispetto ad adesso di qualche minuto, per il
        // consolidamento. Ancorando la finestra all'ULTIMO punto della macchina, dopo di lui
        // non c'e' nessuna casella da riempire: il ritardo si esclude da se'. Senza questo,
        // OGNI macchina sana verrebbe segnalata "non misura da cinque minuti" a ogni apertura,
        // e il riepilogo si imparerebbe a chiudere senza leggerlo.
        Assert.Empty(HistoryStrip.Assenze([.. Serie(Mezzogiorno, 30)], TimeSpan.FromMinutes(30), Minuto));
    }

    [Fact]
    public void LoScartoFraDueOrologiNonInventaInterruzioni()
    {
        // Tutto sta nell'orologio della MACCHINA. Due serie identiche, una traslata di venti
        // minuti - una macchina virtuale, un Windows fuori dominio - devono dire la stessa
        // cosa: uno scarto trasla la serie, non apre buchi in mezzo. Se la finestra si
        // ancorasse all'orologio del CLIENT, la seconda direbbe cose diverse dalla prima.
        List<HistoryPoint> qui = [.. Serie(Mezzogiorno, 10), .. Serie(Mezzogiorno + TimeSpan.FromMinutes(25), 10)];
        List<HistoryPoint> avanti =
        [
            .. Serie(Mezzogiorno + TimeSpan.FromMinutes(20), 10),
            .. Serie(Mezzogiorno + TimeSpan.FromMinutes(45), 10),
        ];

        IReadOnlyList<Assenza> a = HistoryStrip.Assenze(qui, TimeSpan.FromMinutes(35), Minuto);
        IReadOnlyList<Assenza> b = HistoryStrip.Assenze(avanti, TimeSpan.FromMinutes(35), Minuto);

        Assert.Equal(TimeSpan.FromMinutes(15), Assert.Single(a).Durata);
        Assert.Equal(a.Count, b.Count);
        Assert.Equal(a[0].Durata, b[0].Durata);
        Assert.Equal(a[0].DalBordo, b[0].DalBordo);

        // E gli estremi sono traslati esattamente dello scarto, non di piu' e non di meno.
        Assert.Equal(a[0].Inizio + TimeSpan.FromMinutes(20), b[0].Inizio);
    }

    [Fact]
    public void SenzaPuntiNonSiInventaUnInterruzioneLungaQuantoLaFinestra()
    {
        // "Non si sa niente" non e' "e' stata giu' tutto il tempo". La differenza la dice il
        // chiamante con un'altra frase; qui restituire una finestra intera di vuoto sarebbe
        // dichiarare un'interruzione che nessuno ha osservato.
        Assert.Empty(HistoryStrip.Assenze([], TimeSpan.FromHours(1), Minuto));
    }

    [Fact]
    public void IVuotiSiCercanoAlPassoDellaSorgenteNonAQuelloDellaBarra()
    {
        // A sette giorni la barra della striscia copre DUE ORE: un'interruzione di quaranta
        // minuti ci finisce dentro come intervallo parziale e non verrebbe vista affatto.
        // Cercandola al passo della sorgente, cinque minuti, si vede.
        TimeSpan cinque = TimeSpan.FromMinutes(5);
        List<HistoryPoint> punti =
        [
            .. Serie(Mezzogiorno, 2, cinque),
            .. Serie(Mezzogiorno + TimeSpan.FromMinutes(50), 1, cinque),
        ];

        Assenza fine = Assert.Single(HistoryStrip.Assenze(punti, TimeSpan.FromMinutes(55), cinque));

        Assert.Equal(TimeSpan.FromMinutes(40), fine.Durata);
        Assert.False(fine.DalBordo);

        // Lo stesso dato, al passo della barra da due ore: quei quaranta minuti non compaiono
        // piu' da nessuna parte.
        Assert.DoesNotContain(
            HistoryStrip.Assenze(punti, TimeSpan.FromDays(7), TimeSpan.FromHours(2)),
            assenza => assenza.Durata == TimeSpan.FromMinutes(40));
    }
}