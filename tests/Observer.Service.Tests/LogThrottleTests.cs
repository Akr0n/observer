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
public class LogThrottleTests
{
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(5);

    [Fact]
    public void TheFirstFaultIsLogged()
    {
        LogThrottle throttle = Create(out _);

        Assert.True(throttle.ShouldLog("disco-pieno"));
    }

    [Fact]
    public void TheSameReasonIsNotLoggedAgain()
    {
        LogThrottle throttle = Create(out _);

        Assert.True(throttle.ShouldLog("disco-pieno"));
        Assert.False(throttle.ShouldLog("disco-pieno"));
        Assert.False(throttle.ShouldLog("disco-pieno"));
    }

    [Fact]
    public void ADifferentReasonIsLoggedImmediately()
    {
        // Il caso che rende il freno pericoloso se sbagliato: il disco si libera e comincia
        // un guasto d'altra natura. Tacerlo perche' "stiamo gia' segnalando qualcosa"
        // lascerebbe il registro a raccontare il guasto sbagliato.
        LogThrottle throttle = Create(out _);

        Assert.True(throttle.ShouldLog("disco-pieno"));
        Assert.False(throttle.ShouldLog("disco-pieno"));
        Assert.True(throttle.ShouldLog("file-agganciato"));
    }

    [Fact]
    public void AFaultThatLastsIsLoggedAgainAfterTheWindow()
    {
        // Senza questo, un guasto permanente lascia UNA riga e poi silenzio: chi legge il
        // registro un'ora dopo non sa se sta ancora durando. E i numeri dentro il messaggio
        // resterebbero quelli del primo giro.
        LogThrottle throttle = Create(out FakeClock clock);

        Assert.True(throttle.ShouldLog("disco-pieno"));
        clock.Advance(RepeatWindow - TimeSpan.FromSeconds(1));
        Assert.False(throttle.ShouldLog("disco-pieno"));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(throttle.ShouldLog("disco-pieno"));
    }

    [Fact]
    public void AFlickeringFaultDoesNotLogOnEveryRecovery()
    {
        // E' la ragione per cui questa classe non si accontenta di confrontare il motivo.
        // Un collector che ondeggia intorno alla sua scadenza alterna guasto e successo a
        // ogni giro: se ogni ritorno fosse "un motivo nuovo", resterebbe meta' del diluvio.
        LogThrottle throttle = Create(out _);

        Assert.True(throttle.ShouldLog("scaduto"));
        Assert.True(throttle.ShouldLogRecovery(out _));

        for (int round = 0; round < 100; round++)
        {
            Assert.False(throttle.ShouldLog("scaduto"));
            Assert.False(throttle.ShouldLogRecovery(out _));
        }
    }

    [Fact]
    public void TheRecoveryIsLoggedEvenIfTheFaultLastedASingleRound()
    {
        // E' il caso piu' comune, ed e' proprio quello in cui una riga sola lascerebbe
        // credere a un guasto ancora aperto.
        LogThrottle throttle = Create(out _);

        Assert.True(throttle.ShouldLog("disco-pieno"));

        Assert.True(throttle.ShouldLogRecovery(out int silenced));
        Assert.Equal(0, silenced);
    }

    [Fact]
    public void TheRecoverySaysHowManyLinesWereSilenced()
    {
        LogThrottle throttle = Create(out _);

        throttle.ShouldLog("disco-pieno");
        throttle.ShouldLog("disco-pieno");
        throttle.ShouldLog("disco-pieno");

        Assert.True(throttle.ShouldLogRecovery(out int silenced));
        Assert.Equal(2, silenced);
    }

    [Fact]
    public void WithNoFaultThereIsNoRecoveryToAnnounce()
    {
        // Altrimenti ogni giro sano scriverebbe "e' tornato tutto a posto", che e' lo stesso
        // diluvio di prima con parole piu' liete.
        LogThrottle throttle = Create(out _);

        Assert.False(throttle.ShouldLogRecovery(out int silenced));
        Assert.Equal(0, silenced);
    }

    [Fact]
    public void AFaultThatWasNeverLoggedHasNoRecoveryToAnnounce()
    {
        // La fine di una storia che il registro non ha mai cominciato non si racconta: e'
        // l'altra meta' di cio' che tiene silenzioso un guasto che lampeggia.
        LogThrottle throttle = Create(out _);

        throttle.ShouldLog("scaduto");
        throttle.ShouldLogRecovery(out _);

        Assert.False(throttle.ShouldLog("scaduto"));
        Assert.False(throttle.ShouldLogRecovery(out _));
    }

    [Fact]
    public void AfterTheWindowARecurringFaultIsLoggedOnceMore()
    {
        // Il silenzio dell'intermittenza non e' per sempre: passata la finestra, il guasto
        // che ancora va e viene torna a comparire una volta.
        LogThrottle throttle = Create(out FakeClock clock);

        throttle.ShouldLog("scaduto");
        throttle.ShouldLogRecovery(out _);
        Assert.False(throttle.ShouldLog("scaduto"));

        clock.Advance(RepeatWindow);

        Assert.True(throttle.ShouldLog("scaduto"));
    }

    [Fact]
    public void ANullReasonIsNotAReason()
    {
        LogThrottle throttle = Create(out _);

        Assert.Throws<ArgumentNullException>(() => throttle.ShouldLog(null!));
    }

    [Fact]
    public void TheDefaultWindowIsNotZero()
    {
        // Zero renderebbe il freno un pezzo di codice che non frena, e nessuno degli altri
        // test se ne accorgerebbe: li' l'orologio non avanza mai da solo.
        Assert.True(LogThrottle.RepeatInterval >= TimeSpan.FromMinutes(1));
    }

    private static LogThrottle Create(out FakeClock clock)
    {
        clock = new FakeClock();

        return new LogThrottle(clock, RepeatWindow);
    }

    private sealed class FakeClock : TimeProvider
    {
        private long now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => now;

        public void Advance(TimeSpan amount) => now += amount.Ticks;
    }
}
