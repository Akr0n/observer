using Observer.Service;

namespace Observer.Service.Tests;

/// <summary>
/// Il freno che tiene un guasto ripetuto fuori dal registro eventi.
/// </summary>
/// <remarks>
/// Le regole sono due e sono opposte: non ripetere cio' che e' gia' stato detto, e non
/// tacere cio' che e' cambiato. Sbagliare la prima riempie il registro di Windows in poche
/// ore; sbagliare la seconda nasconde il guasto nuovo dietro quello vecchio, che e' peggio.
/// </remarks>
public class FrenoDiRipetizioneTests
{
    [Fact]
    public void IlPrimoSegnalePassa()
    {
        FrenoDiRipetizione freno = new();

        Assert.True(freno.Segnala("disco-pieno"));
    }

    [Fact]
    public void LoStessoMotivoNonPassaPiu()
    {
        FrenoDiRipetizione freno = new();

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
        FrenoDiRipetizione freno = new();

        Assert.True(freno.Segnala("disco-pieno"));
        Assert.False(freno.Segnala("disco-pieno"));
        Assert.True(freno.Segnala("file-agganciato"));
    }

    [Fact]
    public void IlRientroDiceQuanteNeSonoStateTaciute()
    {
        FrenoDiRipetizione freno = new();

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
        FrenoDiRipetizione freno = new();

        Assert.False(freno.Cessato(out int taciute));
        Assert.Equal(0, taciute);
    }

    [Fact]
    public void DopoIlRientroLoStessoMotivoTornaAPassare()
    {
        // Un guasto che va e viene va detto ogni volta che torna: e' proprio l'andirivieni
        // il sintomo, e un freno che lo ricordasse per sempre lo nasconderebbe.
        FrenoDiRipetizione freno = new();

        Assert.True(freno.Segnala("disco-pieno"));
        Assert.True(freno.Cessato(out _));
        Assert.True(freno.Segnala("disco-pieno"));
    }

    [Fact]
    public void UnMotivoNulloNonEUnMotivo()
    {
        FrenoDiRipetizione freno = new();

        Assert.Throws<ArgumentNullException>(() => freno.Segnala(null!));
    }
}
