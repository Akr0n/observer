using Observer.App.Services;

namespace Observer.App.Tests;

/// <summary>
/// La griglia della striscia, e la bugia che esiste per impedire.
/// </summary>
/// <remarks>
/// Il servizio non manda i buchi: un intervallo senza campioni non arriva con zero campioni,
/// non arriva affatto. Chi disegnasse una barretta per ogni punto ricevuto otterrebbe una
/// striscia continua e piena anche da una macchina spenta meta' giornata — i buchi
/// sparirebbero stringendosi, e chi guarda leggerebbe una macchina sempre accesa. E' una
/// bugia raccontata con dati veri: non fallisce niente, e nessun altro test la vedrebbe.
/// </remarks>
public class HistoryStripTests
{
    private static readonly DateTimeOffset Adesso = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Minuto = TimeSpan.FromMinutes(1);

    private static HistoryPoint Punto(int minutiFa, double media, int campioni = 60) =>
        new(Adesso - TimeSpan.FromMinutes(minutiFa), campioni, media, media, media, media);

    [Fact]
    public void UnaBarraParzialeSiDisegnaLargaQuantoHaCoperto()
    {
        // L'ultima barra della striscia e' sempre l'intervallo IN CORSO. A un minuto di passo
        // la differenza non si nota; a due ore, dopo cinque minuti, una barra piena direbbe
        // "due ore cosi'" proprio dove l'occhio legge "adesso".
        HistoryBar unDodicesimo = new(Adesso, BarKind.Parziale, 0.5d, 0.5d, 0.5d, 600, 7200);

        Assert.Equal(10d, HistoryStrip.LarghezzaDi(unDodicesimo, 120d), 9);
    }

    [Fact]
    public void UnaBarraParzialeNonSpariscaMaiDeltutto()
    {
        // Sotto il pixel si leggerebbe come un buco, che vuol dire un'altra cosa: la' non si
        // e' misurato, qui si e' misurato poco.
        HistoryBar appenaNata = new(Adesso, BarKind.Parziale, 0.5d, 0.5d, 0.5d, 1, 7200);

        Assert.Equal(1d, HistoryStrip.LarghezzaDi(appenaNata, 6d), 9);
    }

    [Fact]
    public void LeBarrePieneEIBuchiRestanoLarghiUguali()
    {
        // Stringere una barra piena sarebbe una bugia al contrario, e un buco ha gia' il suo
        // segno: la larghezza parla solo di quanto un intervallo e' stato coperto.
        HistoryBar piena = new(Adesso, BarKind.Misurata, 0.5d, 0.5d, 0.5d, 60, 60);
        HistoryBar buco = new(Adesso, BarKind.Assente, 0d, 0d, 0d, 0, 60);

        Assert.Equal(6d, HistoryStrip.LarghezzaDi(piena, 6d), 9);
        Assert.Equal(6d, HistoryStrip.LarghezzaDi(buco, 6d), 9);
    }

    [Fact]
    public void PiuPuntiNellaStessaBarraSiMedianoInveceDiPerdersi()
    {
        // Il caso che nasce appena il passo della barra supera quello dei punti: un quarto
        // d'ora di barra su punti da cinque minuti. Senza il raggruppamento dentro Costruisci
        // ne sopravviveva UNO — l'ultimo iterato — e la barra mostrava quel campione
        // spacciandolo per la media di tutti e tre. Con tre punti a 0,2, 0,5 e 0,8 la
        // differenza fra la media vera e l'ultimo valore e' l'intera scala.
        List<HistoryPoint> punti =
        [
            Punto(14, 0.2d, campioni: 300),
            Punto(9, 0.5d, campioni: 300),
            Punto(4, 0.8d, campioni: 300),
        ];

        // Due barre: i tre punti cadono tutti nel quarto d'ora PRECEDENTE a quello in corso,
        // perche' Adesso e' allineato alle 12:00 in punto.
        IReadOnlyList<HistoryBar> striscia =
            HistoryStrip.Costruisci(punti, Adesso, quanti: 2, TimeSpan.FromMinutes(15));

        HistoryBar piena = striscia[0];

        Assert.Equal(0.5d, piena.Media, 9);
        Assert.Equal(0.2d, piena.Minimo, 9);
        Assert.Equal(0.8d, piena.Massimo, 9);

        // E i campioni si sommano: 900 su 900, cioe' un quarto d'ora coperto per intero.
        Assert.Equal(900, piena.Campioni);
        Assert.Equal(BarKind.Misurata, piena.Genere);
    }

    [Fact]
    public void UnBucoRestaUnBucoENonSiStringe()
    {
        // IL test. Tre punti su dieci intervalli devono dare DIECI barrette, non tre: sette
        // sono buchi e devono restare al proprio posto nel tempo. Se questa cade, la striscia
        // racconta una macchina sempre accesa a chi l'ha spenta.
        IReadOnlyList<HistoryBar> striscia = HistoryStrip.Costruisci(
            [Punto(9, 0.5d), Punto(5, 0.6d), Punto(0, 0.7d)],
            Adesso,
            quanti: 10,
            Minuto);

        Assert.Equal(10, striscia.Count);
        Assert.Equal(7, striscia.Count(barra => barra.Genere == BarKind.Assente));

        // E stanno esattamente dove devono: il primo, il quinto e l'ultimo.
        Assert.Equal(BarKind.Misurata, striscia[0].Genere);
        Assert.Equal(BarKind.Misurata, striscia[4].Genere);
        Assert.Equal(BarKind.Misurata, striscia[9].Genere);
        Assert.Equal(BarKind.Assente, striscia[1].Genere);
    }

    [Fact]
    public void UnBucoNonHaUnValoreDaMostrare()
    {
        // Un intervallo assente non porta uno zero: uno zero e' una misura, e disegnarlo
        // direbbe "qui la macchina era a riposo" invece di "qui non si sa niente".
        HistoryBar buco = Assert.Single(HistoryStrip.Costruisci([], Adesso, quanti: 1, Minuto));

        Assert.Equal(BarKind.Assente, buco.Genere);
        Assert.Equal(0, buco.Campioni);
    }

    [Fact]
    public void UnIntervalloCopertoAMetaNonSiSpacciaPerCompleto()
    {
        // Misurato sul servizio vero: fermandolo a meta' minuto, quel minuto arriva lo stesso
        // ma con 53 campioni su 60, e con una media calcolata solo su quelli. E' un numero
        // plausibile su mezzo minuto, e va detto che e' mezzo minuto.
        IReadOnlyList<HistoryBar> striscia = HistoryStrip.Costruisci(
            [Punto(0, 0.42d, campioni: 53)],
            Adesso,
            quanti: 1,
            Minuto);

        Assert.Equal(BarKind.Parziale, striscia[0].Genere);
        Assert.Equal(53, striscia[0].Campioni);
        Assert.Equal(60, striscia[0].Attesi);
    }

    [Fact]
    public void UnIstanteFuoriAsseCadeComunqueNellIntervalloGiusto()
    {
        // I timestamp arrivano gia' allineati, ma bastano pochi millisecondi di scarto perche'
        // un confronto per uguaglianza faccia sparire la barretta. E una barretta che sparisce
        // si legge come "non misurato", cioe' il caso peggiore.
        HistoryPoint sbilenco = new(
            Adesso - TimeSpan.FromMinutes(1) + TimeSpan.FromMilliseconds(37),
            60,
            0.33d,
            0.3d,
            0.4d,
            0.35d);

        IReadOnlyList<HistoryBar> striscia = HistoryStrip.Costruisci(
            [sbilenco],
            Adesso,
            quanti: 2,
            Minuto);

        Assert.Equal(BarKind.Misurata, striscia[0].Genere);
        Assert.Equal(0.33d, striscia[0].Media);
    }

    [Fact]
    public void LaStrisciaVaDalPiuVecchioAlPiuRecente()
    {
        IReadOnlyList<HistoryBar> striscia = HistoryStrip.Costruisci([], Adesso, quanti: 3, Minuto);

        Assert.True(striscia[0].Inizio < striscia[1].Inizio);
        Assert.True(striscia[1].Inizio < striscia[2].Inizio);
    }

    [Fact]
    public void RaggruppandoNonSiFaLaMediaDelleMedie()
    {
        // Due intervalli con copertura diversa: 50 campioni a 0.20 e 10 campioni a 0.90.
        // La media vera e' (50*0.20 + 10*0.90) / 60 = 0.3166..., non (0.20+0.90)/2 = 0.55.
        // La media delle medie e' un numero credibile e falso, ed e' l'errore piu' facile.
        IReadOnlyList<HistoryPoint> raggruppati = HistoryStrip.Raggruppa(
            [
                new(Adesso, 50, 0.20d, 0.10d, 0.30d, 0.20d),
                new(Adesso + TimeSpan.FromSeconds(50), 10, 0.90d, 0.80d, 0.95d, 0.90d),
            ],
            Minuto);

        HistoryPoint unito = Assert.Single(raggruppati);

        Assert.Equal(60, unito.Count);
        Assert.Equal(0.31666d, unito.Avg, 4);
        Assert.Equal(0.10d, unito.Min);
        Assert.Equal(0.95d, unito.Max);
    }

    [Fact]
    public void LaCodaFrescaVinceSullAggregatoIndietro()
    {
        // Il consolidamento degli aggregati ha una grazia di quattro minuti, quindi sugli
        // ultimi intervalli l'aggregato e' incompleto. Dove le due letture si sovrappongono
        // deve valere la piu' fresca, altrimenti sarebbe l'aggregato a mentire.
        IReadOnlyList<HistoryPoint> uniti = HistoryStrip.Unisci(
            [new(Adesso, 12, 0.10d, 0.10d, 0.10d, 0.10d)],
            [new(Adesso, 60, 0.80d, 0.70d, 0.90d, 0.85d)]);

        HistoryPoint punto = Assert.Single(uniti);

        Assert.Equal(60, punto.Count);
        Assert.Equal(0.80d, punto.Avg);
    }

    [Fact]
    public void UnIntervalloCompletoNonLoSostituisceUnoTroncato()
    {
        // Il grezzo si chiede da un istante qualsiasi - "dieci minuti fa" - che non cade sul
        // confine di un intervallo, quindi il PRIMO intervallo della coda arriva sempre
        // tagliato. Se vincesse per il solo fatto di essere piu' fresco, una barra misurata
        // per intero si disegnerebbe larga la meta' (e' parziale), il suggerimento direbbe
        // "30 of 60 samples", e media, minimo e massimo salterebbero mezzo minuto di misure:
        // un picco li' dentro sparirebbe. Vince chi ha piu' campioni, non chi arriva dopo.
        IReadOnlyList<HistoryPoint> uniti = HistoryStrip.Unisci(
            [new(Adesso, 60, 0.30d, 0.05d, 0.95d, 0.30d)],
            [new(Adesso, 30, 0.30d, 0.28d, 0.32d, 0.30d)]);

        HistoryPoint punto = Assert.Single(uniti);

        Assert.Equal(60, punto.Count);
        Assert.Equal(0.95d, punto.Max);
    }

    [Fact]
    public void UnendoNonSiPerdonoGliIntervalliCheSoloUnaLetturaHa()
    {
        IReadOnlyList<HistoryPoint> uniti = HistoryStrip.Unisci(
            [new(Adesso - TimeSpan.FromMinutes(30), 60, 0.10d, 0.1d, 0.1d, 0.1d)],
            [new(Adesso, 60, 0.80d, 0.8d, 0.8d, 0.8d)]);

        Assert.Equal(2, uniti.Count);
        Assert.True(uniti[0].Timestamp < uniti[1].Timestamp);
    }

    [Fact]
    public void ICampioniAttesiSeguonoLaDurataDellIntervallo()
    {
        // Il servizio campiona una volta al secondo: e' cio' che rende "quanti campioni sono
        // arrivati" una misura della copertura, e non un dettaglio.
        Assert.Equal(60, HistoryStrip.AttesiIn(TimeSpan.FromMinutes(1)));
        Assert.Equal(300, HistoryStrip.AttesiIn(TimeSpan.FromMinutes(5)));
        Assert.Equal(1, HistoryStrip.AttesiIn(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void UnaStrisciaSenzaIntervalliVieneRifiutata()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => HistoryStrip.Costruisci([], Adesso, quanti: 0, Minuto));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => HistoryStrip.Costruisci([], Adesso, quanti: 5, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(0d, 0)]
    [InlineData(9.9d, 0)]
    [InlineData(10d, 1)]
    [InlineData(99.9d, 9)]
    public void IlPuntatoreCadeNellaBarraGiusta(double x, int atteso) =>
        Assert.Equal(atteso, HistoryStrip.IndiceSotto(x, larghezza: 100d, quante: 10));

    [Theory]
    [InlineData(100d)]
    [InlineData(101d)]
    [InlineData(-1d)]
    public void FuoriDallaStrisciaNonCEUnaBarra(double x)
    {
        // Il bordo destro sbaglia da solo: con x esattamente uguale alla larghezza la
        // divisione da' dieci, cioe' un indice che non esiste, e senza il controllo il
        // suggerimento leggerebbe fuori dall'elenco.
        Assert.Equal(-1, HistoryStrip.IndiceSotto(x, larghezza: 100d, quante: 10));
    }

    [Fact]
    public void IlSuggerimentoDiceLIntervalloNonLIstante()
    {
        // Una barra copre da un minuto a due ore secondo il periodo: mostrarne solo l'inizio
        // lascerebbe indovinare quanto e' larga. Il passo si ricava dalle barre stesse, non da
        // una costante.
        IReadOnlyList<HistoryBar> striscia =
            HistoryStrip.Costruisci([Punto(1, 0.5d)], Adesso, quanti: 3, Minuto);

        Assert.Matches(@"^\d{2}:\d{2} – \d{2}:\d{2}$", HistoryStrip.Descrivi(striscia, 1));
    }

    [Fact]
    public void OltreLaGiornataIlSuggerimentoDiceAncheIlGiorno()
    {
        // A sette giorni la striscia copre 168 ore in 84 barre e non ha assi ne' etichette:
        // il suggerimento e' l'unico modo di collocare una barra nel tempo, e "04:00 – 06:00"
        // da solo compare su SETTE barre, una per giorno. Chi vede un picco - che e' il motivo
        // per cui si guarda una settimana - non saprebbe di che giorno e'. Il nome del giorno
        // basta: fra due barre passano al massimo 166 ore, quindi la coppia non si ripete.
        IReadOnlyList<HistoryBar> settimana =
            HistoryStrip.Costruisci([], Adesso, quanti: 84, TimeSpan.FromHours(2));

        Assert.Matches(@"^[A-Za-z]{3} \d{2}:\d{2} – [A-Za-z]{3} \d{2}:\d{2} · ", HistoryStrip.Descrivi(settimana, 40));

        // A ventiquattro ore l'arco vale esattamente un giorno: la soglia e' stretta, e la
        // frase resta corta dove non serve allungarla.
        IReadOnlyList<HistoryBar> giornata =
            HistoryStrip.Costruisci([], Adesso, quanti: 96, TimeSpan.FromMinutes(15));

        Assert.Matches(@"^\d{2}:\d{2} – \d{2}:\d{2} · ", HistoryStrip.Descrivi(giornata, 40));
    }

    [Fact]
    public void SuUnBucoIlSuggerimentoDiceCheNonSiEMisurato()
    {
        // "Non misurato" non e' "zero", ed e' la stessa distinzione che il disegno fa gia' col
        // tratteggio: qui la si dice a parole, per chi ci passa sopra a controllare.
        IReadOnlyList<HistoryBar> striscia = HistoryStrip.Costruisci([], Adesso, quanti: 3, Minuto);

        Assert.EndsWith("not measured", HistoryStrip.Descrivi(striscia, 0), StringComparison.Ordinal);
    }

    [Fact]
    public void SuUnIntervalloCopertoAMetaIlSuggerimentoDiceQuantiCampioni()
    {
        IReadOnlyList<HistoryBar> striscia =
            HistoryStrip.Costruisci([Punto(1, 0.5d, campioni: 31)], Adesso, quanti: 3, Minuto);

        Assert.EndsWith(
            "31 of 60 samples", HistoryStrip.Descrivi(striscia, 1), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void UnIndiceCheNonEsisteNonProduceUnSuggerimento(int indice)
    {
        IReadOnlyList<HistoryBar> striscia = HistoryStrip.Costruisci([], Adesso, quanti: 3, Minuto);

        Assert.Empty(HistoryStrip.Descrivi(striscia, indice));
    }
}