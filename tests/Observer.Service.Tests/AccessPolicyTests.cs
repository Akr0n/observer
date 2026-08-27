using Observer.Service.LocalChannel;

namespace Observer.Service.Tests;

/// <summary>
/// La decisione di autorizzazione, come tabella esaustiva.
/// </summary>
/// <remarks>
/// Dodici casi, che sono TUTTI i casi: tre modi di essere un chiamante per due portate di
/// endpoint per due esiti del token. Verificarla cosi' costa meno che avviare il servizio, e
/// soprattutto gira identica sui due runner della CI, mentre un canale locale no.
/// </remarks>
public class AccessPolicyTests
{
    [Theory]
    // Il chiamante locale identificato passa SEMPRE, senza token. E' l'obiettivo del progetto.
    [InlineData(CallerKind.LocaleIdentificato, EndpointScope.Ovunque, true, AccessDecision.Consentito)]
    [InlineData(CallerKind.LocaleIdentificato, EndpointScope.Ovunque, false, AccessDecision.Consentito)]
    [InlineData(CallerKind.LocaleIdentificato, EndpointScope.SoloLocale, true, AccessDecision.Consentito)]
    [InlineData(CallerKind.LocaleIdentificato, EndpointScope.SoloLocale, false, AccessDecision.Consentito)]
    // Dalla rete: il token e' l'unica credenziale, come oggi.
    [InlineData(CallerKind.ArrivatoDallaRete, EndpointScope.Ovunque, true, AccessDecision.Consentito)]
    [InlineData(CallerKind.ArrivatoDallaRete, EndpointScope.Ovunque, false, AccessDecision.Rifiutato)]
    // Gli endpoint solo-locali NON esistono per chi non e' locale, nemmeno col token giusto:
    // chi ruba il token non deve poter ruotare le chiavi e chiudere fuori il proprietario.
    [InlineData(CallerKind.ArrivatoDallaRete, EndpointScope.SoloLocale, true, AccessDecision.NonEsiste)]
    [InlineData(CallerKind.ArrivatoDallaRete, EndpointScope.SoloLocale, false, AccessDecision.NonEsiste)]
    // Identita' non determinabile: rifiuto, ANCHE con un token valido.
    [InlineData(CallerKind.NonIdentificabile, EndpointScope.Ovunque, true, AccessDecision.Rifiutato)]
    [InlineData(CallerKind.NonIdentificabile, EndpointScope.Ovunque, false, AccessDecision.Rifiutato)]
    [InlineData(CallerKind.NonIdentificabile, EndpointScope.SoloLocale, true, AccessDecision.NonEsiste)]
    [InlineData(CallerKind.NonIdentificabile, EndpointScope.SoloLocale, false, AccessDecision.NonEsiste)]
    public void LaTabellaCompleta(
        CallerKind chiamante,
        EndpointScope portata,
        bool tokenValido,
        AccessDecision atteso) =>
        Assert.Equal(atteso, AccessPolicy.Decidi(chiamante, portata, tokenValido));

    [Fact]
    public void UnTokenValidoNonSalvaUnChiamanteNonIdentificabile()
    {
        // Il livello di impersonation lo sceglie il CLIENT: con Anonymous un chiamante si rende
        // unilateralmente non identificabile pur restando capace di presentare un token. Se
        // il token bastasse, la regola "l'identita' non determinabile rifiuta" sarebbe vuota.
        Assert.Equal(
            AccessDecision.Rifiutato,
            AccessPolicy.Decidi(CallerKind.NonIdentificabile, EndpointScope.Ovunque, tokenValido: true));
    }

    [Fact]
    public void IValoriZeroDegliEnumSonoQuelliCheNegano()
    {
        // Un campo dimenticato, una struct non inizializzata o un ramo aggiunto per distrazione
        // devono NEGARE. Un endpoint a cui si scordasse la portata diventa irraggiungibile dalla
        // rete, che e' il verso giusto in cui rompersi.
        Assert.Equal(AccessDecision.Rifiutato, default(AccessDecision));
        Assert.Equal(EndpointScope.SoloLocale, default(EndpointScope));
        Assert.Equal(CallerKind.NonIdentificabile, default(CallerKind));

        Assert.Equal(
            AccessDecision.NonEsiste,
            AccessPolicy.Decidi(default, default, tokenValido: false));
    }

    [Fact]
    public void OgniCombinazioneEStataDecisa()
    {
        // Nessun caso resta senza risposta, e nessuno cade in un ramo predefinito per caso.
        foreach (CallerKind chiamante in Enum.GetValues<CallerKind>())
        {
            foreach (EndpointScope portata in Enum.GetValues<EndpointScope>())
            {
                foreach (bool token in new[] { true, false })
                {
                    Assert.True(
                        Enum.IsDefined(AccessPolicy.Decidi(chiamante, portata, token)),
                        $"{chiamante}/{portata}/{token} ha prodotto un esito non definito");
                }
            }
        }
    }
}