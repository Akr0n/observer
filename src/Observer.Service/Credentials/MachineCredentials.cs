using System.Security.Cryptography;
using System.Text;

namespace Observer.Service.Credentials;

/// <summary>
/// Il token di macchina, con la chiave precedente ancora valida per una finestra.
/// </summary>
/// <param name="Current">La chiave corrente.</param>
/// <param name="Previous">La chiave sostituita dall'ultima rotazione, se c'e'.</param>
/// <param name="PreviousExpiresAt">Quando <paramref name="Previous"/> smette di valere.</param>
/// <remarks>
/// Questo token vale DALLA RETE e non scade da solo: e' la ragione per cui il deposito che lo
/// contiene va protetto come un segreto e non come una preferenza.
/// <para>
/// Il ToString() e' sovrascritto perche' i record ne generano uno con TUTTE le proprieta'
/// dentro: senza, basterebbe una riga di log distratta per stampare la chiave.
/// </para>
/// </remarks>
public sealed record MachineCredentials(
    string Current,
    string? Previous,
    DateTimeOffset? PreviousExpiresAt)
{
    /// <summary>Per quanto la chiave precedente resta valida dopo una rotazione.</summary>
    /// <remarks>
    /// Senza questa finestra, ruotare taglierebbe fuori ogni client remoto all'ISTANTE, e la
    /// rotazione diventerebbe un'operazione che nessuno osa fare — cioe' una chiave che non
    /// viene mai cambiata.
    /// </remarks>
    public static readonly TimeSpan FinestraDiGrazia = TimeSpan.FromHours(24);

    /// <summary>Credenziali nuove di zecca, senza chiave precedente.</summary>
    /// <returns>Le credenziali.</returns>
    public static MachineCredentials Nuove() => new(TokenGenerator.Genera(), null, null);

    /// <summary>Se il token presentato e' accettabile in questo istante.</summary>
    /// <param name="presentato">Il token arrivato nell'header.</param>
    /// <param name="adesso">L'istante corrente.</param>
    /// <returns>Vero se corrisponde alla corrente, o alla precedente non ancora scaduta.</returns>
    public bool Accetta(string presentato, DateTimeOffset adesso)
    {
        if (string.IsNullOrEmpty(presentato))
        {
            // Il ramo che un confronto scritto male trasforma in un passaggio libero.
            return false;
        }

        if (Uguali(presentato, Current))
        {
            return true;
        }

        return Previous is { Length: > 0 } precedente
            && PreviousExpiresAt is { } scadenza
            && adesso <= scadenza
            && Uguali(presentato, precedente);
    }

    /// <summary>Genera una chiave nuova conservando quella attuale come precedente.</summary>
    /// <param name="adesso">L'istante della rotazione.</param>
    /// <param name="grazia">Per quanto la chiave attuale continuera' a valere.</param>
    /// <returns>Le credenziali ruotate.</returns>
    /// <remarks>
    /// Si conserva UNA sola chiave precedente. Tenerne una catena significherebbe che una
    /// chiave compromessa resta valida finche' qualcuno non ruota abbastanza volte, cioe' che
    /// la revoca non e' mai immediata.
    /// </remarks>
    public MachineCredentials Ruota(DateTimeOffset adesso, TimeSpan grazia) =>
        new(TokenGenerator.Genera(), Current, adesso + grazia);

    /// <summary>Nasconde le chiavi. Vedi le note del tipo.</summary>
    /// <returns>Una descrizione senza segreti dentro.</returns>
    public override string ToString() =>
        PreviousExpiresAt is { } scadenza
            ? FormattableString.Invariant($"MachineCredentials {{ una chiave corrente, una precedente valida fino a {scadenza:O} }}")
            : "MachineCredentials { una chiave corrente, nessuna precedente }";

    /// <summary>
    /// Confronto a tempo costante: un confronto normale esce al primo byte diverso, e quella
    /// differenza di tempo permette di indovinare il token un carattere alla volta.
    /// </summary>
    private static bool Uguali(string presentato, string atteso) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presentato),
            Encoding.UTF8.GetBytes(atteso));
}