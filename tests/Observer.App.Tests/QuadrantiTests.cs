using Observer.App.Controls;
using Observer.App.Services;
using Observer.App.ViewModels;

namespace Observer.App.Tests;

/// <summary>
/// Le regole su cui poggia la fascia dei quadranti in cima alla finestra.
/// </summary>
/// <remarks>
/// La fascia raccoglie le righe percentuali di tutte le sorgenti e tiene <b>le stesse
/// istanze</b> che stanno nei riquadri sotto, non delle copie. Se un giorno diventassero
/// copie, le lancette si fermerebbero al primo valore letto: un guasto che a schermo si
/// presenta come una macchina tranquilla, cioe' il modo peggiore in cui questo programma
/// possa sbagliare.
/// </remarks>
public class QuadrantiTests
{
    private static MetricGroupState Stato(string collector, params MetricRowState[] righe) =>
        new(collector, collector.ToUpperInvariant(), null, MetricSeverity.Ok, righe);

    private static MetricRowState Fraction(string chiave, string testo, double quanto) =>
        new(chiave, chiave, testo, quanto, MetricSeverity.Ok);

    private static MetricRowState Scritta(string chiave, string testo) =>
        new(chiave, chiave, testo, null, MetricSeverity.Ok);

    [Fact]
    public void AggiornandoUnRiquadroLeRigheRestanoLoStessoOggetto()
    {
        // E' la precondizione della fascia: raccoglie riferimenti una volta sola e si aspetta
        // che continuino a valere. Ricostruire le righe a ogni giro non farebbe fallire niente
        // qui, ma farebbe lampeggiare ogni quadrante una volta al secondo.
        MetricGroup riquadro = new(Stato("cpu", Fraction("cpu.usage.total", "12.0 %", 0.12d)));

        MetricRow prima = riquadro.Rows[0];

        riquadro.Update(Stato("cpu", Fraction("cpu.usage.total", "88.0 %", 0.88d)));

        Assert.Same(prima, riquadro.Rows[0]);
        Assert.Equal("88.0 %", prima.Display);
        Assert.Equal(0.88d, prima.Fraction);
    }

    [Fact]
    public void QuandoLElencoDelleMetricheCambiaLeRigheVengonoRifatte()
    {
        // Il gemello obbligatorio: tenere gli oggetti non deve voler dire tenerli quando non
        // sono piu' gli stessi. Una metrica che compare o sparisce deve rifare l'elenco,
        // altrimenti un quadrante mostrerebbe il valore di un'altra cosa.
        MetricGroup riquadro = new(Stato("memory", Fraction("memory.used.percent", "40.0 %", 0.4d)));

        MetricRow prima = riquadro.Rows[0];

        riquadro.Update(Stato(
            "memory",
            Fraction("memory.used.percent", "41.0 %", 0.41d),
            Scritta("memory.total.bytes", "16.0 GiB")));

        Assert.Equal(2, riquadro.Rows.Count);
        Assert.NotSame(prima, riquadro.Rows[0]);
    }

    [Fact]
    public void UnRiquadroDiSoleFrazioniNonLasciaUnTitoloSospesoSulVuoto()
    {
        // Le frazioni si leggono sul quadrante e non vengono ripetute sotto. Un collector che
        // emette soltanto quelle non deve comparire nella sezione scritta: ci si vedrebbe il
        // suo nome e, sotto, niente.
        MetricGroup soloQuadranti = new(Stato("cpu", Fraction("cpu.usage.total", "12.0 %", 0.12d)));

        Assert.False(soloQuadranti.ShowRows);

        MetricGroup misto = new(Stato(
            "memory",
            Fraction("memory.used.percent", "40.0 %", 0.4d),
            Scritta("memory.total.bytes", "16.0 GiB")));

        Assert.True(misto.ShowRows);
    }

    [Fact]
    public void UnaMetricaCheSmetteDiEssereMisurabilePerdeIlQuadranteEtornaScritta()
    {
        // Il caso che decide se la fascia si aggiorna da sola: la sorgente si degrada, la
        // percentuale non c'e' piu', e quella riga deve smettere di avere un quadrante. Se
        // restasse, mostrerebbe l'ultimo valore buono come se fosse una misura di adesso.
        MetricGroup riquadro = new(Stato("cpu", Fraction("cpu.usage.total", "12.0 %", 0.12d)));

        Assert.True(riquadro.Rows[0].HasGauge);
        Assert.False(riquadro.ShowRows);

        riquadro.Update(Stato("cpu", Scritta("cpu.usage.total", "not measurable")));

        Assert.False(riquadro.Rows[0].HasGauge);
        Assert.True(riquadro.ShowRows);

        // E la frazione resta l'ultima misurata invece di azzerarsi. Non serve a conservarla
        // - il quadrante sparisce comunque - ma la lancetta si anima: uno zero le darebbe un
        // bersaglio, e per qualche decimo di secondo si vedrebbe correre a fondo scala prima
        // di sparire, come se la macchina si fosse svuotata invece che smettere di rispondere.
        Assert.Equal(0.12d, riquadro.Rows[0].Fraction);
    }

    [Fact]
    public void LaCorsaDellaLancettaFinisceEntroUnCampione()
    {
        // L'invariante che tiene insieme due numeri scritti in due file diversi. Una corsa
        // lunga quanto l'intervallo non finirebbe MAI: ogni campione la farebbe ripartire da
        // una posizione interpolata, e il quadrante non starebbe fermo su un valore misurato
        // nemmeno per un istante - mostrerebbe sempre e solo qualcosa di mezzo.
        Assert.True(
            Gauge.NeedleTravelTime < MainViewModel.Interval,
            $"La corsa della lancetta ({Gauge.NeedleTravelTime.TotalMilliseconds} ms) deve restare piu' "
                + $"breve dell'intervallo di campionamento ({MainViewModel.Interval.TotalMilliseconds} ms).");

        // E con un margine vero: a filo, la lancetta arriverebbe giusto mentre parte il
        // campione dopo, e resterebbe ferma zero tempo.
        Assert.True(Gauge.NeedleTravelTime <= MainViewModel.Interval / 2d);
    }
}