using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Observer.App.Services;

namespace Observer.App.ViewModels;

/// <summary>
/// Una riga di metrica a schermo.
/// </summary>
/// <remarks>
/// Deriva da <see cref="ObservableObject"/> e NON da <see cref="ViewModelBase"/>, e il nome
/// non finisce per "ViewModel": entrambe le cose di proposito. ViewLocator aggancia
/// qualunque ViewModelBase e, non trovando una Observer.App.Views.MetricRowView, disegnerebbe
/// un TextBlock "Not Found" al posto della riga. Qui il disegno lo decide il DataTemplate
/// dichiarato in MainWindow.axaml.
/// </remarks>
public sealed partial class MetricRow : ObservableObject
{
    /// <summary>Costruisce la riga dal suo stato.</summary>
    public MetricRow(MetricRowState stato)
    {
        ArgumentNullException.ThrowIfNull(stato);

        Key = stato.Key;
        Label = stato.Label;
        Display = stato.Display;
        HasGauge = stato.Fraction.HasValue;

        // Quando la frazione manca, l'ultima resta dov'e' invece di azzerarsi. Non e' per
        // conservarla - il quadrante sparisce comunque, perche' HasGauge e' falso - ma
        // perche' la lancetta si anima: scrivere zero le darebbe un bersaglio, e per qualche
        // decimo di secondo si vedrebbe scendere a fondo scala prima di sparire, come se la
        // macchina si fosse svuotata invece che smettere di rispondere.
        if (stato.Fraction is { } misurata)
        {
            Fraction = misurata;
        }
        Severity = stato.Severity;
    }

    /// <summary>Identita' stabile della riga.</summary>
    public string Key { get; }

    /// <summary>True quando da questo quadrante si puo' aprire l'elenco dei processi.</summary>
    /// <remarks>
    /// Falso sullo spazio dei dischi, e quel quadrante allora non e' cliccabile affatto: meglio
    /// nessun invito che un invito che porta a un pannello vuoto. Vero sull'attivita' dei
    /// dischi, che apre l'elenco per I/O. Il perche' sta in <see cref="ProcessResource"/>.
    /// </remarks>
    public bool CanShowProcesses => ProcessResource.From(Key) is not null;

    /// <summary>Nome leggibile della metrica.</summary>
    [ObservableProperty]
    public partial string Label { get; set; }

    /// <summary>Display formattato, oppure il motivo per cui manca.</summary>
    [ObservableProperty]
    public partial string Display { get; set; }

    /// <summary>Quanto e' pieno il quadrante, da 0 a 1.</summary>
    /// <remarks>
    /// La stessa frazione che arriva dal servizio, non moltiplicata per cento. Prima veniva
    /// portata a 0..100 per la barra di avanzamento che stava qui; il quadrante lavora sulla
    /// frazione, e la conversione in mezzo era solo un posto in piu' dove sbagliare.
    /// </remarks>
    [ObservableProperty]
    public partial double Fraction { get; set; }

    /// <summary>True quando la metrica e' una frazione e il quadrante ha senso.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHistory))]
    [NotifyPropertyChangedFor(nameof(ShowHistoryNote))]
    public partial bool HasGauge { get; set; }

    /// <summary>Gli intervalli dello storico, dal piu' vecchio al piu' recente.</summary>
    /// <remarks>
    /// Nullo finche' lo storico non e' stato letto. La striscia si mostra solo quando c'e'
    /// qualcosa da mostrare: una striscia tutta vuota sotto un quadrante vivo sarebbe una
    /// domanda senza risposta.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHistory))]
    public partial IReadOnlyList<HistoryBar>? History { get; set; }

    /// <summary>Perche' lo storico non c'e', quando non c'e'.</summary>
    /// <remarks>
    /// Sta qui e non nella barra di stato di proposito: un guasto dello storico NON e' un
    /// guasto della macchina. La macchina puo' rispondere benissimo al campionamento e avere
    /// la persistenza spenta, e colorare di rosso la finestra per questo insegnerebbe a
    /// ignorare anche gli allarmi veri.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHistoryNote))]
    public partial string HistoryNote { get; set; } = string.Empty;

    /// <summary>True quando c'e' una striscia da disegnare.</summary>
    public bool ShowHistory => HasGauge && History is { Count: > 0 };

    /// <summary>True quando c'e' un motivo da scrivere al posto della striscia.</summary>
    public bool ShowHistoryNote => HasGauge && HistoryNote.Length > 0;

    /// <summary>Severity' di cio' che la riga dice.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem))]
    public partial MetricSeverity Severity { get; set; }

    /// <summary>
    /// True solo per un guasto vero. Un Warmup all'avvio o una metrica non misurabile su
    /// questa piattaforma NON devono colorarsi di rosso: sono informazioni, e allarmare chi
    /// guarda per una cosa normale gli insegna a ignorare anche gli allarmi veri.
    /// </summary>
    public bool Problem => Severity == MetricSeverity.Problem;

    /// <summary>Update la riga sul posto, senza ricrearla: evita lo sfarfallio a ogni secondo.</summary>
    public void Update(MetricRowState stato)
    {
        ArgumentNullException.ThrowIfNull(stato);

        Label = stato.Label;
        Display = stato.Display;
        HasGauge = stato.Fraction.HasValue;

        // Quando la frazione manca, l'ultima resta dov'e' invece di azzerarsi. Non e' per
        // conservarla - il quadrante sparisce comunque, perche' HasGauge e' falso - ma
        // perche' la lancetta si anima: scrivere zero le darebbe un bersaglio, e per qualche
        // decimo di secondo si vedrebbe scendere a fondo scala prima di sparire, come se la
        // macchina si fosse svuotata invece che smettere di rispondere.
        if (stato.Fraction is { } misurata)
        {
            Fraction = misurata;
        }
        Severity = stato.Severity;
    }
}

/// <summary>
/// Un riquadro a schermo: un collector con le sue righe. Stesse ragioni di
/// <see cref="MetricRow"/> per non derivare da <see cref="ViewModelBase"/>.
/// </summary>
public sealed partial class MetricGroup : ObservableObject
{
    /// <summary>Costruisce il riquadro dal suo stato.</summary>
    public MetricGroup(MetricGroupState stato)
    {
        ArgumentNullException.ThrowIfNull(stato);

        CollectorId = stato.CollectorId;
        Title = stato.Title;
        Note = stato.Note ?? string.Empty;
        ShowNote = stato.Note is not null;
        Severity = stato.Severity;

        foreach (MetricRowState riga in stato.Rows)
        {
            Rows.Add(new MetricRow(riga));
        }

        RefreshShowRows();
    }

    /// <summary>Identificatore del collector.</summary>
    public string CollectorId { get; }

    /// <summary>Title leggibile del riquadro.</summary>
    [ObservableProperty]
    public partial string Title { get; set; }

    /// <summary>Motivo per cui la sorgente e' degradata.</summary>
    [ObservableProperty]
    public partial string Note { get; set; }

    /// <summary>True quando c'e' una nota da mostrare.</summary>
    [ObservableProperty]
    public partial bool ShowNote { get; set; }

    /// <summary>Severity' dello stato della sorgente.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem))]
    public partial MetricSeverity Severity { get; set; }

    /// <summary>True solo per un guasto vero: vedi <see cref="MetricRow.Problem"/>.</summary>
    public bool Problem => Severity == MetricSeverity.Problem;

    /// <summary>Le righe misurate.</summary>
    public ObservableCollection<MetricRow> Rows { get; } = [];

    /// <summary>True quando questo riquadro ha almeno una riga da SCRIVERE.</summary>
    /// <remarks>
    /// Le metriche che sono una frazione si leggono sul quadrante, in cima, e non vengono
    /// ripetute qui sotto. Un collector che ne emette soltanto di quelle lascerebbe un titolo
    /// sospeso sopra il vuoto: questa proprieta' e' cio' che lo fa sparire.
    /// </remarks>
    [ObservableProperty]
    public partial bool ShowRows { get; set; }

    /// <summary>Update il riquadro sul posto.</summary>
    public void Update(MetricGroupState stato)
    {
        ArgumentNullException.ThrowIfNull(stato);

        Title = stato.Title;
        Note = stato.Note ?? string.Empty;
        ShowNote = stato.Note is not null;
        Severity = stato.Severity;

        // Finche' le chiavi coincidono si aggiorna sul posto; appena l'elenco cambia davvero
        // si ricostruisce. Ricostruire sempre farebbe lampeggiare la finestra ogni secondo.
        if (!HasSameKeys(stato.Rows))
        {
            Rows.Clear();

            foreach (MetricRowState riga in stato.Rows)
            {
                Rows.Add(new MetricRow(riga));
            }

            RefreshShowRows();

            return;
        }

        for (int i = 0; i < stato.Rows.Count; i++)
        {
            Rows[i].Update(stato.Rows[i]);
        }

        RefreshShowRows();
    }

    private void RefreshShowRows() =>
        ShowRows = Rows.Any(riga => !riga.HasGauge);

    private bool HasSameKeys(IReadOnlyList<MetricRowState> stati)
    {
        if (Rows.Count != stati.Count)
        {
            return false;
        }

        for (int i = 0; i < stati.Count; i++)
        {
            if (!string.Equals(Rows[i].Key, stati[i].Key, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
