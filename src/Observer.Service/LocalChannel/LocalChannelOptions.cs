namespace Observer.Service.LocalChannel;

/// <summary>Dove sta il canale locale su questa macchina.</summary>
/// <remarks>
/// Nome della pipe e percorso del socket sono CONFIGURABILI, e non e' una comodita': un
/// endpoint che non si binda abbatte l'INTERO host, endpoint TCP compreso. Con valori fissi,
/// lanciare a mano il servizio su una macchina dove quello installato gira non fallirebbe piu'
/// "solo sulla porta": non partirebbe affatto.
/// </remarks>
public sealed class LocalChannelOptions
{
    /// <summary>Il percorso della sezione in configurazione.</summary>
    public const string SectionName = "Observer:LocalChannel";

    /// <summary>Se aprire il canale locale.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Il nome della named pipe su Windows, senza prefisso.</summary>
    public string PipeName { get; set; } = "Observer";

    /// <summary>Il percorso del socket unix su Linux.</summary>
    public string SocketPath { get; set; } = "/run/observer/observer.sock";

    /// <summary>Si rifiuta di partire con valori inutilizzabili.</summary>
    /// <remarks>
    /// Convalida ENTRAMBI i valori su ogni sistema, non solo quello della piattaforma corrente:
    /// il file di configurazione e' lo stesso su Windows e su Linux, e un refuso nel campo
    /// dell'altro sistema va scoperto da chi lo scrive, non da chi ci arriva dopo.
    /// </remarks>
    public void Validate()
    {
        if (!Enabled)
        {
            // Una macchina che non vuole il canale locale non deve inventarsi valori validi
            // per poter partire.
            return;
        }

        if (string.IsNullOrWhiteSpace(PipeName))
        {
            throw new InvalidOperationException(
                $"{SectionName}:PipeName is empty. Give the pipe a name, or set Enabled to false.");
        }

        if (EndpointUrl.Problema("http://unix:" + SocketPath) is { } problema)
        {
            throw new InvalidOperationException($"{SectionName}:SocketPath can't be used. {problema}");
        }
    }
}