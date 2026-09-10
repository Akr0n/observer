using Observer.Service;

namespace Observer.Service.Tests;

/// <summary>
/// Il freno che tiene un guasto ripetuto fuori dal registro eventi.
/// </summary>
/// <remarks>
/// Le regole sono tre e tirano in direzioni opposte: non ripetere cio' che e' gia' stato
/// detto, non tacere cio' che e' cambiato, e non lasciar credere che un guasto duri ancora
/// quando e' finito. Sbagliare la prima riempie il registro di Windows in poche ore;
/// sbagliare la seconda nasconde il guasto nuovo dietro quello vecchio; sbagliare la terza
/// manda a cercare un guasto che non c'e' piu'.
/// </remarks>
public class FrenoDiRipetizioneTests
{
    private static readonly TimeSpan Finestra = TimeSpan.FromMinutes(5);

    [Fact]
    public void IlPrimoSegnalePassa()
    {
        FrenoDiRipetizione freno = Nuovo(out _);

        Assert.True(freno.Segnala("disco-pieno"));
    }

    [Fact]
    public void LoStessoMotivoNonPassaPiu()
    {
        FrenoDiRipetizione freno = Nuovo(out _);

        Assert.True(freno.Segnala("disco-pieno"));
        Assert.False(freno.Segnala("disco-pieno"));
        Assert.False(freno.Segnala("disco-pieno"));
    }

    [Fact]
    public void UnMotivoDiversoPassaSubito()
    {
        // Il caso che rende il freno pericoloso se sbagliato: il disco si libera e comincia
        // un guasto d'altra natura. Tacerlo perche' "stiamo gia' segnalando qualcosa"
        // lascerebbe il registro a raccontare il guasto sbagliato.
        FrenoDiRipetizione freno = Nuovo(out _);

        Assert.True(freno.Segnala("disco-pieno"));
        Assert.False(freno.Segnala("disco-pieno"));
        Assert.True(freno.Segnala("file-agganciato"));
    }

    [Fact]
    public void UnGuastoCheDuraTornaAScriversiDopoLaFinestra()
    {
        // Senza questo, un guasto permanente lascia UNA riga e poi silenzio: chi legge il
        // registro un'ora dopo non sa se sta ancora durando. E i numeri dentro il messaggio
        // resterebbero quelli del primo giro.
        FrenoDiRipetizione freno = Nuovo(out OrologioFinto orologio);

        Assert.True(freno.Segnala("disco-pieno"));
        orologio.Avanza(Finestra - TimeSpan.FromSeconds(1));
        Assert.False(freno.Segnala("disco-pieno"));
        orologio.Avanza(TimeSpan.FromSeconds(1));
        Assert.True(freno.Segnala("disco-pieno"));
    }

    [Fact]
    public void UnGuastoCheLampeggiaNonScriveAOgniRITORNO()
    {
        // E' la ragione per cui questa classe non si accontenta di confrontare il motivo.
        // Un collector che ondeggia intorno alla sua scadenza alterna guasto e successo a
        // ogni giro: se ogni ritorno fosse "un motivo nuovo", resterebbe meta' del diluvio.
        FrenoDiRipetizione freno = Nuovo(out _);

        Assert.True(freno.Segnala("scaduto"));
        Assert.True(freno.Cessato(out _));

        for (int giro = 0; giro < 100; giro++)
        {
            Assert.False(freno.Segnala("scaduto"));
            Assert.False(freno.Cessato(out _));
        }
    }

    [Fact]
    public void IlRientroSiAnnunciaAncheSeIlGuastoEDuratoUnGiroSolo()
    {
        // E' il caso piu' comune, ed e' proprio quello in cui una riga sola lascerebbe
        // credere a un guasto ancora aperto.
        FrenoDiRipetizione freno = Nuovo(out _);

        Assert.True(freno.Segnala("disco-pieno"));

        Assert.True(freno.Cessato(out int taciute));
        Assert.Equal(0, taciute);
    }

    [Fact]
    public void IlRientroDiceQuanteNeSonoStateTaciute()
    {
        FrenoDiRipetizione freno = Nuovo(out _);

        freno.Segnala("disco-pieno");
        freno.Segnala("disco-pieno");
        freno.Segnala("disco-pieno");

        Assert.True(freno.Cessato(out int taciute));
        Assert.Equal(2, taciute);
    }

    [Fact]
    public void SenzaGuastoNonCEAlcunRientroDaAnnunciare()
    {
        // Altrimenti ogni giro sano scriverebbe "e' tornato tutto a posto", che e' lo stesso
        // diluvio di prima con parole piu' liete.
        FrenoDiRipetizione freno = Nuovo(out _);

        Assert.False(freno.Cessato(out int taciute));
        Assert.Equal(0, taciute);
    }

    [Fact]
    public void UnGuastoMaiScrittoNonAnnunciaIlRientro()
    {
        // La fine di una storia che il registro non ha mai cominciato non si racconta: e'
        // l'altra meta' di cio' che tiene silenzioso un guasto che lampeggia.
        FrenoDiRipetizione freno = Nuovo(out _);

        freno.Segnala("scaduto");
        freno.Cessato(out _);

        Assert.False(freno.Segnala("scaduto"));
        Assert.False(freno.Cessato(out _));
    }

    [Fact]
    public void DopoLaFinestraUnGuastoCheTornaSiFaRisentire()
    {
        // Il silenzio dell'intermittenza non e' per sempre: passata la finestra, il guasto
        // che ancora va e viene torna a comparire una volta.
        FrenoDiRipetizione freno = Nuovo(out OrologioFinto orologio);

        freno.Segnala("scaduto");
        freno.Cessato(out _);
        Assert.False(freno.Segnala("scaduto"));

        orologio.Avanza(Finestra);

        Assert.True(freno.Segnala("scaduto"));
    }

    [Fact]
    public void UnMotivoNulloNonEUnMotivo()
    {
        FrenoDiRipetizione freno = Nuovo(out _);

        Assert.Throws<ArgumentNullException>(() => freno.Segnala(null!));
    }

    [Fact]
    public void LaFinestraPredefinitaNonEZero()
    {
        // Zero renderebbe il freno un pezzo di codice che non frena, e nessuno degli altri
        // test se ne accorgerebbe: li' l'orologio non avanza mai da solo.
        Assert.True(FrenoDiRipetizione.Riepilogo >= TimeSpan.FromMinutes(1));
    }

    private static FrenoDiRipetizione Nuovo(out OrologioFinto orologio)
    {
        orologio = new OrologioFinto();

        return new FrenoDiRipetizione(orologio, Finestra);
    }

    private sealed class OrologioFinto : TimeProvider
    {
        private long adesso;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => adesso;

        public void Avanza(TimeSpan quanto) => adesso += quanto.Ticks;
    }
}
