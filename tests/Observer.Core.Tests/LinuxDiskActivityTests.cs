using Observer.Core.Metrics.Disk;
using Observer.Core.Platform;
using Observer.Core.Platform.Linux;

namespace Observer.Core.Tests;

/// <summary>
/// La lettura di /proc/diskstats e, soprattutto, chi resta fuori.
/// </summary>
/// <remarks>
/// Leggere i contatori e' la parte facile. La parte in cui si sbaglia in silenzio e' decidere
/// di CHI sono: /proc/diskstats elenca insieme dischi interi, partizioni e dispositivi finti,
/// e prenderli tutti conterebbe lo stesso byte due o tre volte — una volta sul disco, una
/// sulla partizione, una sul volume logico. A schermo verrebbe un numero piu' grande del vero
/// che nessuno riconosce come sbagliato.
/// <para>
/// Gira dal runner Windows come da quello Linux: il provider non apre file, li chiede a
/// <see cref="IFileTextReader"/>, e qui il lettore e' finto.
/// </para>
/// </remarks>
public class LinuxDiskActivityTests
{
    // Righe vere accorciate ai 14 campi che contano: major, minor, nome, poi le letture
    // (completate, unite, SETTORI, ms), le scritture (idem), le richieste in corso, i
    // millisecondi occupati e il tempo pesato.
    private const string DiskStats =
        """
        259       0 nvme0n1 1000 0 4000 100 500 0 2000 50 0 750 900
        259       1 nvme0n1p1 900 0 3600 90 400 0 1600 40 0 700 800
          8       0 sda 10 0 20 5 3 0 8 2 0 40 45
          8       1 sda1 9 0 18 4 2 0 6 1 0 35 40
          7       0 loop0 5 0 10 1 0 0 0 0 0 3 3
        """;

    private const int RigheNelCampione = 5;

    [Fact]
    public void ISettoriValgonoSempreCinquecentododiciByte()
    {
        // Non e' la dimensione fisica del blocco: e' un contratto documentato del kernel. Un
        // disco "4K native" li conta ugualmente da 512, e chi moltiplicasse per la dimensione
        // vera pubblicherebbe numeri otto volte piu' grandi.
        DiskStatsLine riga = ProcDiskStatsParser.Read(DiskStats).Single(r => r.Device == "nvme0n1");

        Assert.Equal(4_000UL * 512UL, riga.BytesRead);
        Assert.Equal(2_000UL * 512UL, riga.BytesWritten);
    }

    [Fact]
    public void IlTempoOccupatoEIlCampoDeiTickNonLaSommaDiLetturaEScrittura()
    {
        // Su nvme0n1 la somma dei millisecondi di lettura e scrittura fa 150; il campo giusto
        // e' 750. Prendere la somma e' l'errore che su una finestra vera ha dato 843%.
        DiskStatsLine riga = ProcDiskStatsParser.Read(DiskStats).Single(r => r.Device == "nvme0n1");

        Assert.Equal(TimeSpan.FromMilliseconds(750), riga.Busy);
    }

    [Theory]
    [InlineData("8 0 sda 1 2 3")]
    [InlineData("8 0 xxx a b c d e f g h i j k l")]
    [InlineData("")]
    public void UnaRigaCheNonSiCapisceVieneSaltataSenzaFarCadereLeAltre(string rotta)
    {
        IReadOnlyList<DiskStatsLine> righe = ProcDiskStatsParser.Read(rotta + "\n" + DiskStats);

        Assert.Equal(RigheNelCampione, righe.Count);
        Assert.Contains(righe, r => r.Device == "sda");
    }

    [Fact]
    public void LePartizioniRestanoFuori()
    {
        // Una partizione conta gli stessi byte del disco che la contiene. Il criterio non e'
        // il NOME — "nvme0n1p1" e "sda1" non si somigliano nemmeno, e al primo schema di nomi
        // nuovo un elenco di suffissi sbaglierebbe in silenzio — ma dove il kernel la mette:
        // sotto /sys/block una partizione non compare, sta dentro la cartella del suo disco.
        IReadOnlyList<DiskActivityReading> letture = Leggi();

        Assert.DoesNotContain(letture, l => l.Instance == "nvme0n1p1");
        Assert.DoesNotContain(letture, l => l.Instance == "sda1");
    }

    [Fact]
    public void IDispositiviFintiRestanoFuori()
    {
        // loop0 e' un dispositivo intero a tutti gli effetti: ha il suo /sys/block/loop0/stat.
        // Cio' che non ha e' un dispositivo fisico dietro, ed e' quella la domanda giusta.
        IReadOnlyList<DiskActivityReading> letture = Leggi();

        Assert.DoesNotContain(letture, l => l.Instance == "loop0");
    }

    [Fact]
    public void IDischiVeriRestanoDentro()
    {
        IReadOnlyList<DiskActivityReading> letture = Leggi();

        Assert.Equal(2, letture.Count);
        Assert.Contains(letture, l => l.Instance == "nvme0n1");
        Assert.Contains(letture, l => l.Instance == "sda");
    }

    [Fact]
    public void IlTempoArrivaComeOccupatoNonComeInattivo()
    {
        // Linux conta i tick di occupato, Windows quelli di inattivita'. Se questo provider
        // riempisse il campo sbagliato, la percentuale uscirebbe rovesciata: un disco fermo
        // si mostrerebbe al 100%.
        DiskActivityReading disco = Leggi().Single(l => l.Instance == "sda");

        Assert.Equal(TimeSpan.FromMilliseconds(40), disco.Busy);
        Assert.Null(disco.Idle);
    }

    [Fact]
    public void SenzaProcDiskstatsLaLetturaFallisceInveceDiFingereZeroDischi()
    {
        // "Non sono riuscito a leggere" e "questa macchina non ha dischi" sono due cose
        // diverse, e il collector le tratta diversamente: la prima azzera la storia.
        LettoreFinto lettore = new();

        Assert.False(new LinuxDiskActivityProvider(lettore)
            .TryRead(out IReadOnlyList<DiskActivityReading> letture));

        Assert.Empty(letture);
    }

    private static IReadOnlyList<DiskActivityReading> Leggi()
    {
        LettoreFinto lettore = new();
        lettore.Metti("/proc/diskstats", DiskStats);

        // Presenti per fedelta' alla realta', non perche' il filtro li guardi: i dispositivi
        // interi hanno il proprio /sys/block/NOME/stat, e le partizioni sotto /sys/block non
        // compaiono affatto. E' esattamente il motivo per cui un controllo su questo file
        // non escluderebbe niente in piu' — una mutazione lo ha dimostrato togliendolo senza
        // far fallire nulla, ed e' stato rimosso.
        lettore.Metti("/sys/block/nvme0n1/stat", "");
        lettore.Metti("/sys/block/sda/stat", "");
        lettore.Metti("/sys/block/loop0/stat", "");

        // Il filtro vero: solo chi ha un dispositivo fisico dietro ha device/uevent.
        lettore.Metti("/sys/block/nvme0n1/device/uevent", "DEVTYPE=nvme");
        lettore.Metti("/sys/block/sda/device/uevent", "DEVTYPE=scsi_device");

        Assert.True(new LinuxDiskActivityProvider(lettore)
            .TryRead(out IReadOnlyList<DiskActivityReading> letture));

        return letture;
    }

    private sealed class LettoreFinto : IFileTextReader
    {
        private readonly Dictionary<string, string> file = new(StringComparer.Ordinal);

        public void Metti(string percorso, string contenuto) => file[percorso] = contenuto;

        public bool TryReadAllText(string path, out string content)
        {
            if (file.TryGetValue(path, out string? trovato))
            {
                content = trovato;

                return true;
            }

            content = string.Empty;

            return false;
        }
    }
}