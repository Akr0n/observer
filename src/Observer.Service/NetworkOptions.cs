using System.Globalization;

namespace Observer.Service;

/// <summary>
/// Come il servizio si fa raggiungere dalle ALTRE macchine.
/// </summary>
/// <remarks>
/// Il canale locale non passa di qui: quello non ha ne' porta ne' certificato, e chi guarda la
/// macchina su cui e' seduto non tocca mai la rete.
/// </remarks>
public sealed class NetworkOptions
{
    /// <summary>La sezione di configurazione.</summary>
    public const string SectionName = "Observer:Network";

    /// <summary>La porta HTTPS predefinita.</summary>
    public const int PortaPredefinita = 5058;

    /// <summary>
    /// Se esporre HTTPS alle altre macchine. Acceso di prestazione.
    /// </summary>
    /// <remarks>
    /// Si spegne nei test, dove il trasporto e' finto e generare una chiave RSA a ogni avvio
    /// dell'host costerebbe secondi per niente.
    /// </remarks>
    public bool Https { get; set; } = true;

    /// <summary>La porta su cui ascoltare in HTTPS.</summary>
    public int HttpsPort { get; set; } = PortaPredefinita;

    /// <summary>Controlla le opzioni prima che aprano una porta.</summary>
    /// <exception cref="InvalidOperationException">Se la porta non e' utilizzabile.</exception>
    public void Validate()
    {
        if (Https && HttpsPort is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                "Observer:Network:HttpsPort is " +
                HttpsPort.ToString(CultureInfo.InvariantCulture) +
                ", which is not a usable TCP port. Remove it to use the default (" +
                PortaPredefinita.ToString(CultureInfo.InvariantCulture) + ").");
        }
    }
}
