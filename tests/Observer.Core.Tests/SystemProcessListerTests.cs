using Observer.Core.Processes;

namespace Observer.Core.Tests;

/// <summary>
/// La giunzione fra l'elenco reale dei processi e il lettore dell'I/O.
/// </summary>
/// <remarks>
/// Sul processo VERO che esegue il test, con un lettore finto: la cosa da provare e' che il
/// contatore letto finisca nella riga giusta e che un rifiuto del lettore lasci la riga, senza
/// I/O, invece di toglierla. E' il ramo che nessun altro test attraversa: la classifica usa un
/// elenco finto, i lettori si provano da soli.
/// </remarks>
public class SystemProcessListerTests
{
    [Fact]
    public void IlContatoreFinisceNellaRigaDelProcessoGiusto()
    {
        int mio = Environment.ProcessId;
        LettoreFinto lettore = new(mio, 12_345);

        Assert.True(new SystemProcessLister(lettore).TryList(out IReadOnlyList<ProcessTimes> processi));

        ProcessTimes riga = Assert.Single(processi, processo => processo.Pid == mio);
        Assert.Equal(12_345UL, riga.IoBytes);

        // Gli altri processi il lettore li rifiuta: restano nell'elenco, senza I/O.
        Assert.Contains(processi, processo => processo.Pid != mio && processo.IoBytes is null);
    }

    [Fact]
    public void SenzaLettoreLeRigheCiSonoLoStessoSenzaIo()
    {
        Assert.True(new SystemProcessLister().TryList(out IReadOnlyList<ProcessTimes> processi));

        Assert.Contains(processi, processo => processo.Pid == Environment.ProcessId);
        Assert.All(processi, processo => Assert.Null(processo.IoBytes));
    }

    private sealed class LettoreFinto : IProcessIoReader
    {
        private readonly int pidNoto;
        private readonly ulong valore;

        public LettoreFinto(int pidNoto, ulong valore)
        {
            this.pidNoto = pidNoto;
            this.valore = valore;
        }

        public bool TryRead(int pid, out ulong bytes)
        {
            bytes = pid == pidNoto ? valore : 0;

            return pid == pidNoto;
        }
    }
}