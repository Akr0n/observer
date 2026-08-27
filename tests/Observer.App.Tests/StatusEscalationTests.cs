using Observer.App.Services;

namespace Observer.App.Tests;

/// <summary>
/// Quando un guasto diventa rosso, e quando invece e' ancora normale.
/// </summary>
/// <remarks>
/// Il difetto che questa classe chiude: su una macchina appena installata la finestra si
/// apriva con una barra ROSSA — "Service unreachable" — perche' il primo tentativo cadeva
/// mentre il servizio stava ancora partendo. Cioe' il primo secondo di vita del programma
/// mostrava un errore, e l'errore spariva da solo un attimo dopo. Un allarme che si spegne da
/// solo insegna a ignorare anche quelli veri.
/// <para>
/// La regola non e' "non allarmare mai": e' che la gravita' dipende da QUANTO DURA il guasto,
/// non dal singolo tentativo andato male. Un servizio irraggiungibile da un secondo e' un
/// servizio che sta partendo; da mezzo minuto e' un servizio che non c'e'.
/// </para>
/// </remarks>
public class StatusEscalationTests
{
    private static readonly ObserverEndpoint Locale = ObserverEndpoint.CanaleLocale();

    private static readonly ObserverEndpoint Remoto =
        ObserverEndpoint.Remoto(new Uri("http://altra:5057/"), "t", "dalla prova");

    private static StatusMessage Per(
        ServiceOutcome esito,
        TimeSpan durata,
        ObserverEndpoint punto,
        bool valoriGiaMostrati = false) =>
        StatusEscalation.Per(esito, "dettaglio tecnico dalla prova", durata, punto, valoriGiaMostrati);

    [Fact]
    public void PrimoTentativoAndatoAVuoto_NonEUnErrore()
    {
        // Il caso misurato: la finestra si apre mentre il servizio sta ancora partendo.
        StatusMessage messaggio = Per(ServiceOutcome.NonRaggiungibile, TimeSpan.Zero, Locale);

        Assert.Equal(StatusTone.Informational, messaggio.Tone);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9)]
    public void ServizioIrraggiungibileDaPocoEUnServizioCheSiStaAvviando(int secondi)
    {
        StatusMessage messaggio = Per(ServiceOutcome.NonRaggiungibile, TimeSpan.FromSeconds(secondi), Locale);

        Assert.Equal(StatusTone.Informational, messaggio.Tone);
        Assert.Equal("Connecting", messaggio.Title);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(60)]
    [InlineData(3600)]
    public void ServizioIrraggiungibileDaUnPezzoEUnGuasto(int secondi)
    {
        StatusMessage messaggio = Per(ServiceOutcome.NonRaggiungibile, TimeSpan.FromSeconds(secondi), Locale);

        Assert.Equal(StatusTone.Error, messaggio.Tone);
        Assert.Equal("Service unreachable", messaggio.Title);
    }

    [Fact]
    public void ScadutaLaTolleranza_IlDettaglioTecnicoTornaAGalla()
    {
        // Durante l'attesa il dettaglio si tace perche' e' rumore. Quando il guasto diventa
        // vero il dettaglio serve, ed e' l'unica cosa con cui si diagnostica.
        StatusMessage attesa = Per(ServiceOutcome.NonRaggiungibile, TimeSpan.Zero, Locale);
        StatusMessage guasto = Per(ServiceOutcome.NonRaggiungibile, StatusEscalation.Tolleranza, Locale);

        Assert.DoesNotContain("dettaglio tecnico", attesa.Text, StringComparison.Ordinal);
        Assert.Contains("dettaglio tecnico", guasto.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void SuUnaMacchinaRemota_LAttesaNonDiceCheIlServizioStaPartendo()
    {
        // Di una macchina altrui non si sa se stia partendo: e' un'affermazione che non si
        // puo' fare. Si dice cio' che si sta facendo — contattarla — e basta.
        StatusMessage messaggio = Per(ServiceOutcome.NonRaggiungibile, TimeSpan.Zero, Remoto);

        Assert.Equal(StatusTone.Informational, messaggio.Tone);
        Assert.Contains("altra:5057", messaggio.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("this machine", messaggio.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ServizioCheAscoltaMaNonHaAncoraCampionato_AllInizioENormale()
    {
        StatusMessage messaggio = Per(ServiceOutcome.NonAncoraPronto, TimeSpan.Zero, Locale);

        Assert.Equal(StatusTone.Informational, messaggio.Tone);
    }

    [Fact]
    public void ServizioCheAscoltaMaNonCampionaMai_DiventaUnAvvertimento()
    {
        // Il gemello silenzioso della barra rossa, e altrettanto sbagliato: un servizio vivo
        // che non produce un campione restava "Service is starting" PER SEMPRE, con un testo
        // che promette "questo di solito si risolve da solo in un secondo o due". Se non si
        // risolve, quella frase e' una bugia che nessuno smentisce mai.
        StatusMessage messaggio = Per(ServiceOutcome.NonAncoraPronto, TimeSpan.FromMinutes(5), Locale);

        Assert.Equal(StatusTone.Warning, messaggio.Tone);
        Assert.DoesNotContain("second or two", messaggio.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ServiceOutcome.TokenRifiutato)]
    [InlineData(ServiceOutcome.VersioneIncompatibile)]
    [InlineData(ServiceOutcome.RispostaIncomprensibile)]
    [InlineData(ServiceOutcome.RispostaInattesa)]
    [InlineData(ServiceOutcome.Unknown)]
    public void CioCheNonSiRisolveAspettando_ERossoSubito(ServiceOutcome esito)
    {
        // Aspettare aiuta solo dove aspettare puo' cambiare l'esito. Un token sbagliato, una
        // versione incompatibile o una risposta illeggibile saranno identici fra un minuto:
        // rimandare l'allarme rimanderebbe solo il momento in cui l'utente puo' agire.
        StatusMessage subito = Per(esito, TimeSpan.Zero, Remoto);

        Assert.Equal(StatusTone.Error, subito.Tone);
        Assert.Contains("dettaglio tecnico", subito.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void SenzaValoriASchermo_LaRigaSottoIlTitoloNonNeInventa()
    {
        StatusMessage attesa = Per(ServiceOutcome.NonRaggiungibile, TimeSpan.Zero, Locale);
        StatusMessage guasto = Per(ServiceOutcome.NonRaggiungibile, TimeSpan.FromMinutes(1), Locale);

        Assert.DoesNotContain("last successful reading", attesa.Subheading, StringComparison.Ordinal);
        Assert.DoesNotContain("last successful reading", guasto.Subheading, StringComparison.Ordinal);
        Assert.Equal("Not connected.", guasto.Subheading);
    }

    [Fact]
    public void ConValoriASchermo_LaRigaSottoIlTitoloDiceCheSonoFermi()
    {
        // Lasciare i valori a schermo senza dirlo li farebbe leggere come attuali: e' il modo
        // piu' facile di far credere che una macchina stia bene mentre e' spenta.
        StatusMessage guasto = Per(
            ServiceOutcome.NonRaggiungibile,
            TimeSpan.FromMinutes(1),
            Locale,
            valoriGiaMostrati: true);

        Assert.Contains("last successful reading", guasto.Subheading, StringComparison.Ordinal);
    }

    [Fact]
    public void NessunEsitoProduceUnaBarraVuota()
    {
        // Una barra visibile senza titolo o senza testo e' un riquadro colorato che non dice
        // niente, ed e' peggio di nessuna barra.
        foreach (ServiceOutcome esito in Enum.GetValues<ServiceOutcome>())
        {
            if (esito == ServiceOutcome.Ok)
            {
                continue;
            }

            foreach (TimeSpan durata in new[] { TimeSpan.Zero, TimeSpan.FromHours(1) })
            {
                StatusMessage messaggio = Per(esito, durata, Locale);

                Assert.False(string.IsNullOrWhiteSpace(messaggio.Title), $"{esito} a {durata}: titolo vuoto");
                Assert.False(string.IsNullOrWhiteSpace(messaggio.Text), $"{esito} a {durata}: testo vuoto");
                Assert.False(
                    string.IsNullOrWhiteSpace(messaggio.Subheading),
                    $"{esito} a {durata}: sottotitolo vuoto");
            }
        }
    }
}