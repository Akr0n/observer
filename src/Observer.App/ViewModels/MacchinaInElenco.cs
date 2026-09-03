using CommunityToolkit.Mvvm.ComponentModel;
using Observer.App.Services;

namespace Observer.App.ViewModels;

/// <summary>Come sta una macchina dell'elenco, per il pallino accanto al nome.</summary>
public enum StatoVoce
{
    /// <summary>Non e' ancora stata interrogata. Grigio.</summary>
    Ignoto = 0,

    /// <summary>L'ultima lettura e' andata. Verde.</summary>
    Raggiungibile = 1,

    /// <summary>Non risponde da poco, o risponde con un avviso. Giallo.</summary>
    Attenzione = 2,

    /// <summary>Guasto vero, secondo la stessa regola della barra di stato. Rosso.</summary>
    Guasto = 3,
}

/// <summary>
/// Una voce della barra laterale: la macchina, e come sta.
/// </summary>
/// <remarks>
/// Deriva da <see cref="ObservableObject"/> e NON da <see cref="ViewModelBase"/>, per la stessa
/// ragione di <see cref="MetricRow"/>: ViewLocator aggancia qualunque ViewModelBase e
/// disegnerebbe un "Not Found" al posto della riga.
/// <para>
/// Lo stato segue la regola della barra di stato - <see cref="StatusEscalation"/>, con la sua
/// grazia di dieci secondi - cosi' un pallino rosso vuol dire la stessa cosa di una barra
/// rossa. Prima della barra laterale con i pallini, per sapere come stava una macchina
/// bisognava cliccarci sopra.
/// </para>
/// </remarks>
public sealed partial class MacchinaInElenco : ObservableObject
{
    /// <summary>Costruisce la voce, ancora senza stato.</summary>
    /// <param name="punto">La macchina.</param>
    public MacchinaInElenco(ObserverEndpoint punto)
    {
        ArgumentNullException.ThrowIfNull(punto);

        Punto = punto;
    }

    /// <summary>La macchina.</summary>
    public ObserverEndpoint Punto { get; }

    /// <summary>Il nome scritto nell'elenco.</summary>
    public string Nome => Punto.NomeVisibile;

    /// <summary>Da quando le letture falliscono di fila, oppure null se l'ultima e' andata.</summary>
    /// <remarks>La stessa misura che la barra di stato tiene per la macchina guardata.</remarks>
    internal DateTimeOffset? GuastoDa { get; set; }

    /// <summary>True mentre una sonda e' in volo: la prossima non le parte sopra.</summary>
    internal bool InSonda { get; set; }

    /// <summary>Come sta.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Ignoto), nameof(Raggiungibile), nameof(Attenzione), nameof(Guasto), nameof(Descrizione))]
    public partial StatoVoce Stato { get; set; }

    /// <summary>Perche' sta cosi', in una frase corta: il titolo che avrebbe la barra di stato.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Descrizione))]
    public partial string Dettaglio { get; set; } = "Not checked yet";

    /// <summary>True finche' nessuno l'ha interrogata.</summary>
    public bool Ignoto => Stato == StatoVoce.Ignoto;

    /// <summary>True quando l'ultima lettura e' andata.</summary>
    public bool Raggiungibile => Stato == StatoVoce.Raggiungibile;

    /// <summary>True quando c'e' un problema che potrebbe ancora passare da solo.</summary>
    public bool Attenzione => Stato == StatoVoce.Attenzione;

    /// <summary>True su un guasto vero.</summary>
    public bool Guasto => Stato == StatoVoce.Guasto;

    /// <summary>Nome e stato insieme, per chi non vede il pallino.</summary>
    public string Descrizione => $"{Nome}: {Dettaglio}";

    /// <summary>Registra l'esito di una lettura, dalla sonda o dal giro principale.</summary>
    /// <param name="esito">Com'e' andata.</param>
    /// <param name="problema">La frase del client, quando non e' andata.</param>
    /// <param name="adesso">L'ora, per misurare da quanto dura un guasto.</param>
    public void Registra(ServiceOutcome esito, string problema, DateTimeOffset adesso)
    {
        if (esito == ServiceOutcome.Ok)
        {
            GuastoDa = null;
            Stato = StatoVoce.Raggiungibile;
            Dettaglio = "Reachable";

            return;
        }

        GuastoDa ??= adesso;

        StatusMessage messaggio = StatusEscalation.Per(
            esito, problema, adesso - GuastoDa.Value, Punto, valoriGiaMostrati: false);

        Stato = messaggio.Tone == StatusTone.Error ? StatoVoce.Guasto : StatoVoce.Attenzione;
        Dettaglio = messaggio.Title;
    }
}