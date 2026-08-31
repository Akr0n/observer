using Observer.Core.Units;

namespace Observer.Core.Metrics.Disk;

/// <summary>
/// I contatori cumulativi di UN dispositivo: quanto ha letto, quanto ha scritto, e da quanto
/// tempo sta lavorando.
/// </summary>
/// <remarks>
/// Sono contatori dall'accensione, non misure: da soli non dicono niente di utile. Il valore
/// nasce dalla differenza fra due letture, ed e' <see cref="DiskActivityRates"/> a farla.
/// <para>
/// Il tempo di lavoro arriva dai due lati opposti a seconda della piattaforma, e per questo
/// ci sono due fabbriche invece di un campo solo: Windows conta i tick di INATTIVITA', Linux
/// i tick di OCCUPATO. Sono la stessa grandezza vista dal verso opposto, e appiattirle qui
/// dentro vorrebbe dire che una delle due parti mente.
/// </para>
/// </remarks>
public readonly record struct DiskActivityReading
{
    private DiskActivityReading(
        string instance,
        ulong bytesRead,
        ulong bytesWritten,
        TimeSpan? busy,
        TimeSpan? idle)
    {
        Instance = instance;
        BytesRead = bytesRead;
        BytesWritten = bytesWritten;
        Busy = busy;
        Idle = idle;
    }

    /// <summary>
    /// Come si chiama il dispositivo per chi guarda: <c>Disk 0</c> su Windows, <c>sda</c> su
    /// Linux. Deve restare stabile da un campione all'altro, o la serie si spezza in due.
    /// </summary>
    /// <remarks>
    /// E' un DISPOSITIVO, non un volume: <c>C:</c> e <c>Disk 0</c> non sono la stessa cosa e
    /// la corrispondenza fra i due non e' uno a uno. Legarli richiederebbe di attraversare le
    /// partizioni, e una riga che dicesse "C:" mostrando il traffico di due volumi sarebbe
    /// peggio di una riga che dice onestamente "Disk 0".
    /// </remarks>
    public string Instance { get; }

    /// <summary>Byte letti dall'accensione.</summary>
    public ulong BytesRead { get; }

    /// <summary>Byte scritti dall'accensione.</summary>
    public ulong BytesWritten { get; }

    /// <summary>Tempo cumulativo in cui il dispositivo aveva richieste in corso, se noto.</summary>
    public TimeSpan? Busy { get; }

    /// <summary>Tempo cumulativo in cui il dispositivo non aveva niente da fare, se noto.</summary>
    public TimeSpan? Idle { get; }

    /// <summary>Costruisce una lettura da una piattaforma che conta il tempo OCCUPATO.</summary>
    /// <param name="instance">Nome del dispositivo.</param>
    /// <param name="bytesRead">Byte letti dall'accensione.</param>
    /// <param name="bytesWritten">Byte scritti dall'accensione.</param>
    /// <param name="busy">Tempo cumulativo con richieste in corso.</param>
    /// <returns>La lettura.</returns>
    public static DiskActivityReading ConTempoOccupato(
        string instance,
        ulong bytesRead,
        ulong bytesWritten,
        TimeSpan busy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instance);
        NonNegativo(busy, nameof(busy));

        return new DiskActivityReading(instance, bytesRead, bytesWritten, busy, idle: null);
    }

    /// <summary>Costruisce una lettura da una piattaforma che conta il tempo INATTIVO.</summary>
    /// <param name="instance">Nome del dispositivo.</param>
    /// <param name="bytesRead">Byte letti dall'accensione.</param>
    /// <param name="bytesWritten">Byte scritti dall'accensione.</param>
    /// <param name="idle">Tempo cumulativo senza richieste in corso.</param>
    /// <returns>La lettura.</returns>
    public static DiskActivityReading ConTempoInattivo(
        string instance,
        ulong bytesRead,
        ulong bytesWritten,
        TimeSpan idle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instance);
        NonNegativo(idle, nameof(idle));

        return new DiskActivityReading(instance, bytesRead, bytesWritten, busy: null, idle);
    }

    private static void NonNegativo(TimeSpan quanto, string nome)
    {
        if (quanto < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nome, quanto, "a cumulative time cannot be negative");
        }
    }
}

/// <summary>
/// Porta di lettura dei contatori di attivita' dei dischi.
/// </summary>
/// <remarks>
/// Separata da <see cref="IDiskReadingProvider"/> di proposito, anche se parla degli stessi
/// oggetti fisici: quella misura lo spazio sui VOLUMI, questa il traffico sui DISPOSITIVI, e
/// le due cose si leggono da posti diversi con nomi diversi. Un'unica porta obbligherebbe una
/// delle due a fingere di conoscere l'altra.
/// </remarks>
public interface IDiskActivityProvider
{
    /// <summary>Falso quando su questa piattaforma non si misura affatto.</summary>
    bool IsSupported { get; }

    /// <summary>Perche' non si misura, quando non si misura.</summary>
    string? UnsupportedReason { get; }

    /// <summary>Legge i contatori. False quando la lettura fallisce del tutto.</summary>
    bool TryRead(out IReadOnlyList<DiskActivityReading> readings);
}

/// <summary>
/// Da due letture successive ai numeri da mostrare.
/// </summary>
/// <remarks>
/// Funzione pura e separata dal collector per la stessa ragione di <c>CpuUsage</c>: qui ogni
/// modo di sbagliare produce un numero credibile, e un numero credibile e falso non lo trova
/// nessuno guardando la finestra.
/// </remarks>
public static class DiskActivityRates
{
    /// <summary>Byte al secondo fra due letture dello stesso contatore.</summary>
    /// <param name="previous">Contatore al campione precedente.</param>
    /// <param name="current">Contatore adesso.</param>
    /// <param name="elapsed">Tempo trascorso fra i due.</param>
    /// <param name="rate">Il tasso, valorizzato solo se il calcolo riesce.</param>
    /// <param name="failure">Perche' non si e' potuto calcolare.</param>
    /// <returns>True se il tasso e' utilizzabile.</returns>
    public static bool TryComputeBytesPerSecond(
        ulong previous,
        ulong current,
        TimeSpan elapsed,
        out double rate,
        out SampleFailure failure)
    {
        rate = 0d;

        if (elapsed <= TimeSpan.Zero)
        {
            // Non e' pedanteria: dividere per zero qui non solleva un errore, produce
            // infinito, e MetricValue.FromNumber lancia sui valori non finiti — perdendo
            // l'intera risposta HTTP per colpa di un disco solo.
            failure = SampleFailure.NoElapsedTime;

            return false;
        }

        if (current < previous)
        {
            failure = SampleFailure.CounterWentBackwards;

            return false;
        }

        double valore = (current - previous) / elapsed.TotalSeconds;

        if (!double.IsFinite(valore))
        {
            failure = SampleFailure.NotFinite;

            return false;
        }

        rate = valore;
        failure = SampleFailure.Unknown;

        return true;
    }

    /// <summary>Quanto e' stato occupato il dispositivo, in percentuale del tempo trascorso.</summary>
    /// <param name="previous">Lettura precedente.</param>
    /// <param name="current">Lettura attuale.</param>
    /// <param name="elapsed">Tempo trascorso fra le due.</param>
    /// <param name="busy">L'occupazione, valorizzata solo se il calcolo riesce.</param>
    /// <param name="failure">Perche' non si e' potuto calcolare.</param>
    /// <returns>True se l'occupazione e' utilizzabile.</returns>
    /// <remarks>
    /// <b>Non</b> si sommano il tempo di lettura e quello di scrittura. Le due code si
    /// sovrappongono, e su una stessa finestra quella somma ha gia' dato 843%: un numero che
    /// nessuno riconosce come sbagliato finche' non supera cento.
    /// </remarks>
    public static bool TryComputeBusy(
        DiskActivityReading previous,
        DiskActivityReading current,
        TimeSpan elapsed,
        out Percent busy,
        out SampleFailure failure)
    {
        busy = default;

        if (elapsed <= TimeSpan.Zero)
        {
            failure = SampleFailure.NoElapsedTime;

            return false;
        }

        double rapporto;

        if (previous.Busy is { } occupatoPrima && current.Busy is { } occupatoAdesso)
        {
            if (occupatoAdesso < occupatoPrima)
            {
                failure = SampleFailure.CounterWentBackwards;

                return false;
            }

            rapporto = (occupatoAdesso - occupatoPrima) / elapsed;
        }
        else if (previous.Idle is { } fermoPrima && current.Idle is { } fermoAdesso)
        {
            if (fermoAdesso < fermoPrima)
            {
                failure = SampleFailure.CounterWentBackwards;

                return false;
            }

            rapporto = 1d - ((fermoAdesso - fermoPrima) / elapsed);
        }
        else
        {
            // Irraggiungibile passando dalle due fabbriche, che valorizzano sempre uno dei
            // due tempi. Ci si arriva solo con una lettura costruita come default, e in quel
            // caso non c'e' davvero nessuna diagnosi da dare.
            failure = SampleFailure.Unknown;

            return false;
        }

        if (!double.IsFinite(rapporto))
        {
            failure = SampleFailure.NotFinite;

            return false;
        }

        // I due estremi sono misurati, non teorici. Sotto zero: su un disco FERMO
        // l'inattivita' avanza di un filo piu' dell'intervallo, perche' non e' lo stesso
        // orologio a contarli, e su questa macchina il calcolo dava -0,07% — che
        // Percent.TryFromRatio rifiuta, trasformando un disco fermo in un guasto. Sopra
        // cento: con piu' richieste in coda i tick di occupato superano l'intervallo, e un
        // disco non e' occupato al 150%, e' occupato.
        if (!Percent.TryFromRatio(Math.Clamp(rapporto, 0d, 1d), out busy))
        {
            failure = SampleFailure.NotFinite;

            return false;
        }

        failure = SampleFailure.Unknown;

        return true;
    }
}