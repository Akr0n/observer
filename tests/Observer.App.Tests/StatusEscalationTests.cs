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
    private static readonly ObserverEndpoint Local = ObserverEndpoint.LocalChannel();

    private static readonly ObserverEndpoint Remote =
        ObserverEndpoint.Remote(new Uri("http://altra:5057/"), "t", "dalla prova");

    private static StatusMessage MessageFor(
        ServiceOutcome outcome,
        TimeSpan failingFor,
        ObserverEndpoint endpoint,
        bool hasValuesOnScreen = false) =>
        StatusEscalation.MessageFor(outcome, "dettaglio tecnico dalla prova", failingFor, endpoint, hasValuesOnScreen);

    [Fact]
    public void TheFirstFailedAttempt_IsNotAnError()
    {
        // Il caso misurato: la finestra si apre mentre il servizio sta ancora partendo.
        StatusMessage message = MessageFor(ServiceOutcome.Unreachable, TimeSpan.Zero, Local);

        Assert.Equal(StatusTone.Informational, message.Tone);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9)]
    public void AServiceUnreachableForAShortTimeIsAServiceThatIsStarting(int seconds)
    {
        StatusMessage message = MessageFor(ServiceOutcome.Unreachable, TimeSpan.FromSeconds(seconds), Local);

        Assert.Equal(StatusTone.Informational, message.Tone);
        Assert.Equal("Connecting", message.Title);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(60)]
    [InlineData(3600)]
    public void AServiceUnreachableForAWhileIsAFault(int seconds)
    {
        StatusMessage message = MessageFor(ServiceOutcome.Unreachable, TimeSpan.FromSeconds(seconds), Local);

        Assert.Equal(StatusTone.Error, message.Tone);
        Assert.Equal("Service unreachable", message.Title);
    }

    [Fact]
    public void OnceTheGraceHasExpired_TheTechnicalDetailComesBack()
    {
        // Durante l'attesa il dettaglio si tace perche' e' rumore. Quando il guasto diventa
        // vero il dettaglio serve, ed e' l'unica cosa con cui si diagnostica.
        StatusMessage waiting = MessageFor(ServiceOutcome.Unreachable, TimeSpan.Zero, Local);
        StatusMessage fault = MessageFor(ServiceOutcome.Unreachable, StatusEscalation.GracePeriod, Local);

        Assert.DoesNotContain("dettaglio tecnico", waiting.Text, StringComparison.Ordinal);
        Assert.Contains("dettaglio tecnico", fault.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void OnARemoteMachine_TheWaitNamesThatMachineAndNotThisOne()
    {
        // Di una macchina altrui non si sa se stia partendo: e' un'affermazione che non si
        // puo' fare. Si dice cio' che si sta facendo — contattarla — e basta.
        StatusMessage message = MessageFor(ServiceOutcome.Unreachable, TimeSpan.Zero, Remote);

        Assert.Equal(StatusTone.Informational, message.Tone);
        Assert.Contains("altra:5057", message.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("this machine", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AServiceThatListensButHasNotSampledYet_IsNormalAtFirst()
    {
        StatusMessage message = MessageFor(ServiceOutcome.NotReadyYet, TimeSpan.Zero, Local);

        Assert.Equal(StatusTone.Informational, message.Tone);
    }

    [Fact]
    public void AServiceThatListensButNeverSamples_BecomesAWarning()
    {
        // Il gemello silenzioso della barra rossa, e altrettanto sbagliato: un servizio vivo
        // che non produce un campione restava "Service is starting" PER SEMPRE, con un testo
        // che promette "questo di solito si risolve da solo in un secondo o due". Se non si
        // risolve, quella frase e' una bugia che nessuno smentisce mai.
        StatusMessage message = MessageFor(ServiceOutcome.NotReadyYet, TimeSpan.FromMinutes(5), Local);

        Assert.Equal(StatusTone.Warning, message.Tone);
        Assert.DoesNotContain("second or two", message.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ServiceOutcome.TokenRejected)]
    [InlineData(ServiceOutcome.IncompatibleVersion)]
    [InlineData(ServiceOutcome.UnreadableResponse)]
    [InlineData(ServiceOutcome.UnexpectedResponse)]
    [InlineData(ServiceOutcome.Unknown)]
    public void WhatWaitingCannotFix_IsRedStraightAway(ServiceOutcome outcome)
    {
        // Aspettare aiuta solo dove aspettare puo' cambiare l'esito. Un token sbagliato, una
        // versione incompatibile o una risposta illeggibile saranno identici fra un minuto:
        // rimandare l'allarme rimanderebbe solo il momento in cui l'utente puo' agire.
        StatusMessage immediate = MessageFor(outcome, TimeSpan.Zero, Remote);

        Assert.Equal(StatusTone.Error, immediate.Tone);
        Assert.Contains("dettaglio tecnico", immediate.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoValuesOnScreen_TheSubheadingDoesNotInventAny()
    {
        StatusMessage waiting = MessageFor(ServiceOutcome.Unreachable, TimeSpan.Zero, Local);
        StatusMessage fault = MessageFor(ServiceOutcome.Unreachable, TimeSpan.FromMinutes(1), Local);

        Assert.DoesNotContain("last successful reading", waiting.Subheading, StringComparison.Ordinal);
        Assert.DoesNotContain("last successful reading", fault.Subheading, StringComparison.Ordinal);
        Assert.Equal("Not connected.", fault.Subheading);
    }

    [Fact]
    public void WithValuesOnScreen_TheSubheadingSaysTheyAreStale()
    {
        // Lasciare i valori a schermo senza dirlo li farebbe leggere come attuali: e' il modo
        // piu' facile di far credere che una macchina stia bene mentre e' spenta.
        StatusMessage fault = MessageFor(
            ServiceOutcome.Unreachable,
            TimeSpan.FromMinutes(1),
            Local,
            hasValuesOnScreen: true);

        Assert.Contains("last successful reading", fault.Subheading, StringComparison.Ordinal);
    }

    [Fact]
    public void NoOutcomeProducesAnEmptyBar()
    {
        // Una barra visibile senza titolo o senza testo e' un riquadro colorato che non dice
        // niente, ed e' peggio di nessuna barra.
        foreach (ServiceOutcome outcome in Enum.GetValues<ServiceOutcome>())
        {
            if (outcome == ServiceOutcome.Ok)
            {
                continue;
            }

            foreach (TimeSpan failingFor in new[] { TimeSpan.Zero, TimeSpan.FromHours(1) })
            {
                StatusMessage message = MessageFor(outcome, failingFor, Local);

                Assert.False(string.IsNullOrWhiteSpace(message.Title), $"{outcome} a {failingFor}: titolo vuoto");
                Assert.False(string.IsNullOrWhiteSpace(message.Text), $"{outcome} a {failingFor}: testo vuoto");
                Assert.False(
                    string.IsNullOrWhiteSpace(message.Subheading),
                    $"{outcome} a {failingFor}: sottotitolo vuoto");
            }
        }
    }

    [Theory]
    [InlineData(ServiceOutcome.ConnectionRefused)]
    [InlineData(ServiceOutcome.TimedOut)]
    public void BothWaysOfNotAnsweringDeserveTheGraceToo(ServiceOutcome outcome)
    {
        // Appena avviata, una macchina rifiuta la connessione perche' la porta non e' ancora
        // aperta, e piu' avanti nell'avvio la accetta. Togliere la tolleranza a questi due
        // rimetterebbe la barra rossa all'apertura della finestra, che e' il difetto che
        // questa classe esiste per chiudere.
        StatusMessage message = MessageFor(outcome, TimeSpan.Zero, Remote);

        Assert.Equal(StatusTone.Informational, message.Tone);
        Assert.Equal("Connecting", message.Title);
    }

    [Fact]
    public void ARefusalAndATimeoutDoNotReadTheSame()
    {
        // Il cuore di questa correzione. I due guasti hanno rimedi opposti: uno si risolve
        // avviando un servizio, l'altro aprendo una porta. Se il titolo e' lo stesso, chi
        // guarda la finestra non ha nient'altro da cui capirlo.
        StatusMessage refusal = MessageFor(ServiceOutcome.ConnectionRefused, TimeSpan.FromMinutes(1), Remote);
        StatusMessage expired = MessageFor(ServiceOutcome.TimedOut, TimeSpan.FromMinutes(1), Remote);

        Assert.Equal(StatusTone.Error, refusal.Tone);
        Assert.Equal(StatusTone.Error, expired.Tone);
        Assert.NotEqual(refusal.Title, expired.Title);
        Assert.NotEqual("Service unreachable", refusal.Title);
        Assert.NotEqual("Service unreachable", expired.Title);
    }

    [Fact]
    public void EveryWayOfFailingHasATitleOfItsOwn()
    {
        // La falla che questo test chiude non si vede a schermo: e' l'arm di scarto in fondo
        // allo switch. Aggiungere un valore all'enum COMPILA, e il guasto nuovo finisce in
        // silenzio sotto un titolo generico, con la tolleranza tolta senza che nessuno lo
        // abbia deciso. Nessun test falliva. Ora fallisce questo.
        List<string> withGenericTitle = [];

        foreach (ServiceOutcome outcome in Enum.GetValues<ServiceOutcome>())
        {
            if (outcome is ServiceOutcome.Ok or ServiceOutcome.Unknown)
            {
                continue;
            }

            if (MessageFor(outcome, TimeSpan.FromHours(1), Remote).Title == "Reading failed")
            {
                withGenericTitle.Add(outcome.ToString());
            }
        }

        Assert.True(
            withGenericTitle.Count == 0,
            "questi esiti finiscono sotto il titolo generico invece di avere il proprio: "
                + string.Join(", ", withGenericTitle));
    }
}