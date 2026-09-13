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
    [InlineData(CallerKind.LocalIdentified, EndpointScope.Anywhere, true, AccessDecision.Allowed)]
    [InlineData(CallerKind.LocalIdentified, EndpointScope.Anywhere, false, AccessDecision.Allowed)]
    [InlineData(CallerKind.LocalIdentified, EndpointScope.LocalOnly, true, AccessDecision.Allowed)]
    [InlineData(CallerKind.LocalIdentified, EndpointScope.LocalOnly, false, AccessDecision.Allowed)]
    // Dalla rete: il token e' l'unica credenziale, come oggi.
    [InlineData(CallerKind.FromNetwork, EndpointScope.Anywhere, true, AccessDecision.Allowed)]
    [InlineData(CallerKind.FromNetwork, EndpointScope.Anywhere, false, AccessDecision.Denied)]
    // Gli endpoint solo-locali NON esistono per chi non e' locale, nemmeno col token giusto:
    // chi ruba il token non deve poter ruotare le chiavi e chiudere fuori il proprietario.
    [InlineData(CallerKind.FromNetwork, EndpointScope.LocalOnly, true, AccessDecision.NotFound)]
    [InlineData(CallerKind.FromNetwork, EndpointScope.LocalOnly, false, AccessDecision.NotFound)]
    // Identita' non determinabile: rifiuto, ANCHE con un token valido.
    [InlineData(CallerKind.Unidentified, EndpointScope.Anywhere, true, AccessDecision.Denied)]
    [InlineData(CallerKind.Unidentified, EndpointScope.Anywhere, false, AccessDecision.Denied)]
    [InlineData(CallerKind.Unidentified, EndpointScope.LocalOnly, true, AccessDecision.NotFound)]
    [InlineData(CallerKind.Unidentified, EndpointScope.LocalOnly, false, AccessDecision.NotFound)]
    public void LaTabellaCompleta(
        CallerKind chiamante,
        EndpointScope portata,
        bool tokenIsValid,
        AccessDecision atteso) =>
        Assert.Equal(atteso, AccessPolicy.Decide(chiamante, portata, tokenIsValid));

    [Fact]
    public void UnTokenValidoNonSalvaUnChiamanteNonIdentificabile()
    {
        // Il livello di impersonation lo sceglie il CLIENT: con Anonymous un chiamante si rende
        // unilateralmente non identificabile pur restando capace di presentare un token. Se
        // il token bastasse, la regola "l'identita' non determinabile rifiuta" sarebbe vuota.
        Assert.Equal(
            AccessDecision.Denied,
            AccessPolicy.Decide(CallerKind.Unidentified, EndpointScope.Anywhere, tokenIsValid: true));
    }

    [Fact]
    public void IValoriZeroDegliEnumSonoQuelliCheNegano()
    {
        // Un campo dimenticato, una struct non inizializzata o un ramo aggiunto per distrazione
        // devono NEGARE. Un endpoint a cui si scordasse la portata diventa irraggiungibile dalla
        // rete, che e' il verso giusto in cui rompersi.
        Assert.Equal(AccessDecision.Denied, default(AccessDecision));
        Assert.Equal(EndpointScope.LocalOnly, default(EndpointScope));
        Assert.Equal(CallerKind.Unidentified, default(CallerKind));

        Assert.Equal(
            AccessDecision.NotFound,
            AccessPolicy.Decide(default, default, tokenIsValid: false));
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
                        Enum.IsDefined(AccessPolicy.Decide(chiamante, portata, token)),
                        $"{chiamante}/{portata}/{token} ha prodotto un esito non definito");
                }
            }
        }
    }
}