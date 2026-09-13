using System.ComponentModel;
using System.Diagnostics;
using Observer.Core.Units;

namespace Observer.Core.Processes;

/// <summary>The raw counters of ONE process, as the operating system gives them.</summary>
/// <param name="Pid">Process identifier.</param>
/// <param name="Name">Name of the executable, without the path.</param>
/// <param name="Cpu">Processor time consumed since the process started.</param>
/// <param name="WorkingSet">Physical memory occupied right now.</param>
/// <param name="IoBytes">
/// Bytes read and written by the process since it started, through the I/O calls: files, pipes
/// and sockets together, reads served from the cache included. Null when the system would not
/// tell them — on Linux that is the norm for another user's processes.
/// </param>
public readonly record struct ProcessTimes(
    int Pid, string Name, TimeSpan Cpu, ByteSize WorkingSet, ulong? IoBytes = null);

/// <summary>How much a process is consuming, ready to show.</summary>
/// <param name="Pid">Process identifier.</param>
/// <param name="Name">Name of the executable.</param>
/// <param name="CpuPercent">
/// CPU percentage over the WHOLE machine, not over one core: 100 means every core busy. Null
/// when it is not known yet — on the first round, or for a just-born process — which is
/// different from zero and must not be confused with "it is idle".
/// </param>
/// <param name="WorkingSet">Physical memory occupied.</param>
/// <param name="IoBytesPerSecond">
/// Bytes per second read and written by the process, over the last interval. Null for the same
/// reasons as the CPU — first round, just-born process — and on top of that when the system
/// does not provide the counter.
/// </param>
public readonly record struct ProcessUsage(
    int Pid, string Name, double? CpuPercent, ByteSize WorkingSet, double? IoBytesPerSecond = null);

/// <summary>Read port for the process list.</summary>
public interface IProcessLister
{
    /// <summary>Reads the processes. False when the list cannot be obtained at all.</summary>
    /// <param name="processes">The processes read.</param>
    /// <returns>True if the read succeeded.</returns>
    bool TryList(out IReadOnlyList<ProcessTimes> processes);
}

/// <summary>Read port for the I/O counter of ONE process.</summary>
/// <remarks>
/// Separate from the list because it is the only part that is not portable: name, memory and
/// processor time are given by the standard library on both systems, the transferred bytes are
/// not.
/// </remarks>
public interface IProcessIoReader
{
    /// <summary>Reads the bytes read and written by the process since it started.</summary>
    /// <param name="pid">Process identifier.</param>
    /// <param name="bytes">The total, reads plus writes.</param>
    /// <returns>False when the system does not tell it, for that process.</returns>
    bool TryRead(int pid, out ulong bytes);
}

/// <summary>
/// The real adapter, on top of <see cref="Process"/>.
/// </summary>
/// <remarks>
/// One single adapter for both platforms, and that is not laziness: name, memory occupied and
/// processor time are already portable in the standard library. Per-process I/O is not, and it
/// comes from an <see cref="IProcessIoReader"/> per operating system, optional: without one,
/// that column stays unknown and the rest of the list does not suffer for it.
/// <para>
/// A process that vanishes between the listing and the reading of its counters does NOT make
/// the others fail: it just disappears. That is the norm, not the exception — on a live machine
/// something dies all the time, and a list that refused to answer because of that would be
/// unusable exactly when it is needed.
/// </para>
/// </remarks>
public sealed class SystemProcessLister : IProcessLister
{
    private readonly IProcessIoReader? io;

    /// <summary>Creates the adapter, with or without the I/O reader.</summary>
    /// <param name="ioReader">Where to read the transferred bytes from, or null not to read them.</param>
    public SystemProcessLister(IProcessIoReader? ioReader = null)
    {
        io = ioReader;
    }

    /// <inheritdoc />
    public bool TryList(out IReadOnlyList<ProcessTimes> processes)
    {
        List<ProcessTimes> found = [];

        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                if (TryReadProcess(process, io, out ProcessTimes reading))
                {
                    found.Add(reading);
                }
            }
        }

        processes = found;

        return true;
    }

    private static bool TryReadProcess(Process process, IProcessIoReader? io, out ProcessTimes reading)
    {
        reading = default;

        try
        {
            int pid = process.Id;
            string name = process.ProcessName;
            TimeSpan cpu = process.TotalProcessorTime;
            ByteSize memory = ByteSize.FromBytes(process.WorkingSet64);

            // I/O is read last - AFTER the name and the counters, into local variables, not as a
            // constructor argument, where it would be evaluated first - and it does not make the
            // row fail: a process whose CPU is known but whose transferred bytes are not is
            // still a process to show.
            ulong? transferred = io is not null && io.TryRead(pid, out ulong ioBytes)
                ? ioBytes
                : null;

            reading = new ProcessTimes(pid, name, cpu, memory, transferred);

            return true;
        }
        catch (InvalidOperationException)
        {
            // Ended between the enumeration and the reading of its counters.
            return false;
        }
        catch (Win32Exception)
        {
            // On Windows protected processes refuse the processor time even to LocalSystem; on
            // Linux it happens for other users' processes. Out of the list: better one row less
            // than a list that does not arrive.
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }
}