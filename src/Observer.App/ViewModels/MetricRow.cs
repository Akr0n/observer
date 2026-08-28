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
        Etichetta = stato.Label;
        Valore = stato.Display;
        Frazione = stato.Fraction ?? 0d;
        HaQuadrante = stato.Fraction.HasValue;
        Gravita = stato.Severity;
    }

    /// <summary>Identita' stabile della riga.</summary>
    public string Key { get; }

    /// <summary>Nome leggibile della metrica.</summary>
    [ObservableProperty]
    public partial string Etichetta { get; set; }

    /// <summary>Valore formattato, oppure il motivo per cui manca.</summary>
    [ObservableProperty]
    public partial string Valore { get; set; }

    /// <summary>Quanto e' pieno il quadrante, da 0 a 1.</summary>
    /// <remarks>
    /// La stessa frazione che arriva dal servizio, non moltiplicata per cento. Prima veniva
    /// portata a 0..100 per la barra di avanzamento che stava qui; il quadrante lavora sulla
    /// frazione, e la conversione in mezzo era solo un posto in piu' dove sbagliare.
    /// </remarks>
    [ObservableProperty]
    public partial double Frazione { get; set; }

    /// <summary>True quando la metrica e' una frazione e il quadrante ha senso.</summary>
    [ObservableProperty]
    public partial bool HaQuadrante { get; set; }

    /// <summary>Gravita' di cio' che la riga dice.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problema))]
    public partial MetricSeverity Gravita { get; set; }

    /// <summary>
    /// True solo per un guasto vero. Un Warmup all'avvio o una metrica non misurabile su
    /// questa piattaforma NON devono colorarsi di rosso: sono informazioni, e allarmare chi
    /// guarda per una cosa normale gli insegna a ignorare anche gli allarmi veri.
    /// </summary>
    public bool Problema => Gravita == MetricSeverity.Problema;

    /// <summary>Aggiorna la riga sul posto, senza ricrearla: evita lo sfarfallio a ogni secondo.</summary>
    public void Aggiorna(MetricRowState stato)
    {
        ArgumentNullException.ThrowIfNull(stato);

        Etichetta = stato.Label;
        Valore = stato.Display;
        Frazione = stato.Fraction ?? 0d;
        HaQuadrante = stato.Fraction.HasValue;
        Gravita = stato.Severity;
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
        Titolo = stato.Title;
        Nota = stato.Note ?? string.Empty;
        MostraNota = stato.Note is not null;
        Gravita = stato.Severity;

        foreach (MetricRowState riga in stato.Rows)
        {
            Righe.Add(new MetricRow(riga));
        }

        ContaLeRigheDaScrivere();
    }

    /// <summary>Identificatore del collector.</summary>
    public string CollectorId { get; }

    /// <summary>Titolo leggibile del riquadro.</summary>
    [ObservableProperty]
    public partial string Titolo { get; set; }

    /// <summary>Motivo per cui la sorgente e' degradata.</summary>
    [ObservableProperty]
    public partial string Nota { get; set; }

    /// <summary>True quando c'e' una nota da mostrare.</summary>
    [ObservableProperty]
    public partial bool MostraNota { get; set; }

    /// <summary>Gravita' dello stato della sorgente.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problema))]
    public partial MetricSeverity Gravita { get; set; }

    /// <summary>True solo per un guasto vero: vedi <see cref="MetricRow.Problema"/>.</summary>
    public bool Problema => Gravita == MetricSeverity.Problema;

    /// <summary>Le righe misurate.</summary>
    public ObservableCollection<MetricRow> Righe { get; } = [];

    /// <summary>True quando questo riquadro ha almeno una riga da SCRIVERE.</summary>
    /// <remarks>
    /// Le metriche che sono una frazione si leggono sul quadrante, in cima, e non vengono
    /// ripetute qui sotto. Un collector che ne emette soltanto di quelle lascerebbe un titolo
    /// sospeso sopra il vuoto: questa proprieta' e' cio' che lo fa sparire.
    /// </remarks>
    [ObservableProperty]
    public partial bool MostraRighe { get; set; }

    /// <summary>Aggiorna il riquadro sul posto.</summary>
    public void Aggiorna(MetricGroupState stato)
    {
        ArgumentNullException.ThrowIfNull(stato);

        Titolo = stato.Title;
        Nota = stato.Note ?? string.Empty;
        MostraNota = stato.Note is not null;
        Gravita = stato.Severity;

        // Finche' le chiavi coincidono si aggiorna sul posto; appena l'elenco cambia davvero
        // si ricostruisce. Ricostruire sempre farebbe lampeggiare la finestra ogni secondo.
        if (!StesseChiavi(stato.Rows))
        {
            Righe.Clear();

            foreach (MetricRowState riga in stato.Rows)
            {
                Righe.Add(new MetricRow(riga));
            }

            ContaLeRigheDaScrivere();

            return;
        }

        for (int i = 0; i < stato.Rows.Count; i++)
        {
            Righe[i].Aggiorna(stato.Rows[i]);
        }

        ContaLeRigheDaScrivere();
    }

    private void ContaLeRigheDaScrivere() =>
        MostraRighe = Righe.Any(riga => !riga.HaQuadrante);

    private bool StesseChiavi(IReadOnlyList<MetricRowState> stati)
    {
        if (Righe.Count != stati.Count)
        {
            return false;
        }

        for (int i = 0; i < stati.Count; i++)
        {
            if (!string.Equals(Righe[i].Key, stati[i].Key, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
