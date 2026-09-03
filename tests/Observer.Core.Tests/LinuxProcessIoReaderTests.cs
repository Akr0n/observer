using Observer.Core.Platform;
using Observer.Core.Platform.Linux;

namespace Observer.Core.Tests;

/// <summary>
/// La lettura di <c>/proc/PID/io</c>, su un lettore finto: gira su entrambi i runner.
/// </summary>
/// <remarks>
/// La regola che conta e' QUALI righe si sommano. <c>read_bytes</c> e <c>write_bytes</c> sono
/// li' accanto, sembrano piu' giusti per un pannello aperto da un quadrante di disco, e sono la
/// scelta sbagliata: Windows non ha l'equivalente, e i due sistemi devono dire la stessa cosa.
/// </remarks>
public class LinuxProcessIoReaderTests
{
    // Un file vero, con i sette campi nell'ordine in cui il kernel li scrive.
    private const string ProcIo =
        """
        rchar: 3000
        wchar: 500
        syscr: 40
        syscw: 10
        read_bytes: 8192
        write_bytes: 4096
        cancelled_write_bytes: 0
        """;

    [Fact]
    public void SommaRcharEWcharENonIByteSuDisco()
    {
        LettoreFinto lettore = new();
        lettore.Metti("/proc/42/io", ProcIo);

        Assert.True(new LinuxProcessIoReader(lettore).TryRead(42, out ulong byteIo));
        Assert.Equal(3500UL, byteIo);
    }

    [Fact]
    public void UnProcessoCheNonSiPuoLeggereNonHaUnContatore()
    {
        // Su Linux e' il caso normale: il processo di un altro utente, senza CAP_SYS_PTRACE.
        // Il lettore vede un file che non si apre, e la risposta e' "non lo so", non zero.
        LettoreFinto lettore = new();

        Assert.False(new LinuxProcessIoReader(lettore).TryRead(42, out ulong byteIo));
        Assert.Equal(0UL, byteIo);
    }

    [Fact]
    public void IlPercorsoUsaIlPidChiesto()
    {
        LettoreFinto lettore = new();
        lettore.Metti("/proc/7/io", ProcIo);

        Assert.False(new LinuxProcessIoReader(lettore).TryRead(42, out _));
        Assert.True(new LinuxProcessIoReader(lettore).TryRead(7, out _));
    }

    [Theory]
    [InlineData("wchar: 500\nsyscr: 1")]
    [InlineData("rchar: 3000\nsyscr: 1")]
    [InlineData("rchar: tanti\nwchar: 500")]
    [InlineData("rchar: -1\nwchar: 500")]
    [InlineData("")]
    public void SenzaEntrambiICampiInteriNonCEUnContatore(string contenuto)
    {
        Assert.False(LinuxProcessIoReader.TryParse(contenuto, out ulong byteIo));
        Assert.Equal(0UL, byteIo);
    }

    [Fact]
    public void UnaSommaCheFaIlGiroDeiSessantaquattroBitNonEUnTotale()
    {
        string contenuto = "rchar: 18446744073709551615\nwchar: 1";

        Assert.False(LinuxProcessIoReader.TryParse(contenuto, out _));
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