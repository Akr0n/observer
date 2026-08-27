namespace Observer.Service.LocalChannel;

/// <summary>Cosa fare di una richiesta.</summary>
/// <remarks>
/// Il valore ZERO e' <see cref="Rifiutato"/>: un campo dimenticato o un ramo aggiunto per
/// distrazione negano invece di concedere.
/// </remarks>
public enum AccessDecision
{
    /// <summary>401. La credenziale manca o non basta.</summary>
    Rifiutato = 0,

    /// <summary>404. L'endpoint non deve nemmeno risultare esistente a questo chiamante.</summary>
    NonEsiste,

    /// <summary>La richiesta prosegue.</summary>
    Consentito,
}

/// <summary>Da dove un endpoint accetta di essere raggiunto.</summary>
/// <remarks>
/// Il valore ZERO e' <see cref="SoloLocale"/>, cioe' il piu' restrittivo: un endpoint a cui
/// qualcuno scordasse di dichiarare la portata diventa irraggiungibile dalla rete invece che
/// esposto, che e' il verso giusto in cui rompersi.
/// </remarks>
public enum EndpointScope
{
    /// <summary>Solo dal canale locale. Non esiste, per chi arriva da altrove.</summary>
    SoloLocale = 0,

    /// <summary>Anche dalla rete, col token.</summary>
    Ovunque,
}

/// <summary>
/// Decide se una richiesta passa. Funzione PURA: nessuno stato, nessuna I/O.
/// </summary>
/// <remarks>
/// Sostituisce il middleware che pretendeva il bearer token su ogni richiesta. Essendo pura si
/// verifica con una tabella esaustiva che gira identica sui due runner della CI, mentre un
/// canale locale no: su ubuntu-latest la named pipe non esiste nemmeno.
/// <para>
/// QUALI utenti locali siano ammessi non lo decide questa funzione. Lo decide il sistema
/// operativo: su Windows la DACL della pipe, che rifiuta gia' alla connect; su Linux il modo del
/// file del socket. Qui si verificano due cose soltanto - che il chiamante sia davvero locale e
/// che sia identificabile. Aggiungerci una lista di SID duplicherebbe una decisione che il
/// sistema operativo prende meglio.
/// </para>
/// </remarks>
public static class AccessPolicy
{
    /// <summary>L'esito per questa combinazione.</summary>
    /// <param name="chiamante">Come e' stato classificato chi chiama.</param>
    /// <param name="portata">Da dove l'endpoint accetta di essere raggiunto.</param>
    /// <param name="tokenValido">Se il bearer token presentato corrisponde.</param>
    /// <returns>Cosa fare della richiesta.</returns>
    public static AccessDecision Decidi(CallerKind chiamante, EndpointScope portata, bool tokenValido)
    {
        // Il chiamante locale identificato passa su tutto, senza token. E' l'obiettivo del
        // progetto: sulla macchina il sistema operativo sa gia' chi chiama, e un segreto
        // condiviso e' lo strumento sbagliato.
        if (chiamante == CallerKind.LocaleIdentificato)
        {
            return AccessDecision.Consentito;
        }

        // Da qui in giu' il chiamante NON e' un locale identificato.
        // Un endpoint solo-locale non deve nemmeno risultare esistente: gli endpoint di
        // appaiamento ruotano le chiavi, e chi rubasse il token non deve poter chiudere fuori
        // il proprietario. Un 403 confermerebbe che l'endpoint c'e'; un 404 no.
        if (portata != EndpointScope.Ovunque)
        {
            return AccessDecision.NonEsiste;
        }

        // Identita' non determinabile: rifiuto ANCHE con un token valido. Il livello di
        // impersonation lo sceglie il CLIENT, e con Anonymous un chiamante si rende
        // unilateralmente non identificabile pur restando capace di presentare un token. Se il
        // token bastasse, la regola "l'identita' non determinabile rifiuta" sarebbe vuota.
        // Chi ha il token puo' sempre usare il canale di rete.
        if (chiamante != CallerKind.ArrivatoDallaRete)
        {
            return AccessDecision.Rifiutato;
        }

        return tokenValido ? AccessDecision.Consentito : AccessDecision.Rifiutato;
    }
}