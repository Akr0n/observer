using System.ComponentModel;
using System.Diagnostics;

using Observer.Core.Processes;
using Observer.Service.LocalChannel;

namespace Observer.Service;

/// <summary>What a process row looks like on the wire.</summary>
/// <param name="Pid">The process identifier.</param>
/// <param name="Name">The executable's name.</param>
/// <param name="CpuPercent">
/// Percentage of the whole machine, or null when it is not known yet. Null and not zero:
/// the client must be able to show a dash instead of claiming the process is idle.
/// </param>
/// <param name="WorkingSetBytes">Physical memory in use.</param>
/// <param name="IoBytesPerSecond">
/// Bytes per second read and written, or null when it is not known: first round, a process just
/// born, or a system that does not tell - on Linux, other users' processes.
/// </param>
public sealed record ProcessRow(
    int Pid, string Name, double? CpuPercent, long WorkingSetBytes, double? IoBytesPerSecond);

/// <summary>The response of <c>/processes</c>.</summary>
/// <param name="CapturedAt">When the list was read.</param>
/// <param name="By">
/// The criterion applied: <c>cpu</c>, <c>memory</c> or <c>io</c>. Repeated on purpose: a client
/// that asks an older service for a criterion it does not know would receive the CPU list,
/// and without this field it would show it under the wrong title.
/// </param>
/// <param name="Processes">The processes, already sorted.</param>
public sealed record ProcessListResponse(
    DateTimeOffset CapturedAt, string By, IReadOnlyList<ProcessRow> Processes);

/// <summary>The endpoints that say who is consuming the machine, and allow stopping it.</summary>
/// <remarks>
/// <b>Terminating a process is the only thing this service does that is not a read.</b>
/// Up to here Observer exposed telemetry: a stolen token let you see someone else's CPU. With
/// this endpoint the same token stops processes on that machine, and the service runs as
/// LocalSystem. The scope stays <c>Anywhere</c> by an explicit decision of the project's
/// owner, not by omission — restricting it to the local channel alone would be a single line,
/// and the consequence of not writing it is that the token is worth much more than before.
/// <para>
/// That is why every attempt is logged with the PID, the name and the caller's origin, both
/// when it succeeds and when the system refuses it: an action that destroys state must leave
/// a trace, and without one it would be the only irreversible thing in the project to have none.
/// </para>
/// <para>
/// <b>And that is why the caller must name what it means to stop.</b> A pid on its own is not an
/// identity: it is a number the system reuses, and the one the caller is holding came from a
/// list that is a second old at best. The name travels with the request and is compared with the
/// live process before anything is signalled. The check is where the kill HAPPENS, not in the
/// dashboard that asks for it: a confirmation in a window is a courtesy to the person clicking,
/// not a control over what a token holder can send.
/// </para>
/// <para>
/// Every way this can fail refuses rather than proceeds - no name, the wrong name, a name that
/// cannot be read at all because the process went away or the system would not say. "I cannot
/// prove this is the right process" and "this is the wrong process" lead to the same place, and
/// neither of them leads to <see cref="Process.Kill()"/>.
/// </para>
/// <para>
/// <b>What the comparison buys, and what it does not.</b> It refuses a pid that has become a
/// DIFFERENT program. It does not refuse a pid that has become another copy of the SAME program
/// - every instance of chrome is called chrome - and the processes that free pids fastest are
/// exactly the ones that come in copies. So a kill whose answer never arrived is safer to send
/// again than it was, not safe: the second attempt can no longer hit something unrelated, but it
/// can hit the next chrome. Closing that needs something unique to one process, its start time
/// beside its name, and that is a change rather than a correction: the list would have to carry
/// it, and on Linux the start time is not readable for every process.
/// </para>
/// </remarks>
public static partial class ProcessEndpoints
{
    /// <summary>How many processes are returned when the request does not say.</summary>
    private const int DefaultTop = 15;

    /// <summary>The most that can be returned, so as not to send the whole process table.</summary>
    private const int MaxTop = 100;


    /// <summary>Maps /processes and /processes/{pid}/kill.</summary>
    /// <param name="endpoints">The application's route builder.</param>
    public static void MapProcessEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/processes", (ProcessRanking ranking, string? by, int? top) =>
            ListProcesses(ranking, by, top));

        endpoints.MapPost("/processes/{pid:int}/kill", (
            HttpContext context,
            ILoggerFactory loggerFactory,
            int pid,
            string? name) => Terminate(context, loggerFactory, pid, name));
    }

    private static IResult ListProcesses(ProcessRanking ranking, string? by, int? top)
    {
        if (!ranking.TryRead(out IReadOnlyList<ProcessUsage> processes))
        {
            return Results.Problem(
                detail: "the process list could not be read on this machine",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        int count = Math.Clamp(top ?? DefaultTop, 1, MaxTop);

        // By memory, by I/O or by CPU. Whoever says nothing gets the CPU, which is the
        // question you ask yourself looking at a red gauge.
        string criterion = NormalizeCriterion(by);

        IReadOnlyList<ProcessUsage> sorted = criterion switch
        {
            "memory" => ProcessRanking.TopByMemory(processes, count),
            "io" => ProcessRanking.TopByIo(processes, count),
            _ => ProcessRanking.TopByCpu(processes, count),
        };

        return Results.Ok(new ProcessListResponse(
            DateTimeOffset.UtcNow,
            criterion,
            [.. sorted.Select(process => new ProcessRow(
                process.Pid,
                process.Name,
                process.CpuPercent,
                process.WorkingSet.Bytes,
                process.IoBytesPerSecond))]));
    }

    private static string NormalizeCriterion(string? by) => by?.ToUpperInvariant() switch
    {
        "MEMORY" => "memory",
        "IO" => "io",
        _ => "cpu",
    };

    private static IResult Terminate(
        HttpContext context, ILoggerFactory loggerFactory, int pid, string? requestedName)
    {
        ILogger logger = loggerFactory.CreateLogger(typeof(ProcessEndpoints).FullName!);
        CallerOrigin origin = LocalCaller.Classify(context);

        // A pid is not an identity. The list the caller is looking at was read a second ago at
        // best, and a confirmation click before that at worst; in between the process can end
        // and the system is free to hand the number to another one. So the caller has to say
        // WHAT it meant to stop, and a request that says nothing is refused rather than carried
        // out on whatever holds the number now. Refusing is also what makes a dashboard older
        // than this service stop working LOUDLY, instead of going on killing by pid in silence.
        // What counts as a usable name is ProcessNameRule, in Core, because the dashboard has to
        // apply the same rule before sending - see the remarks there.
        if (!ProcessNameRule.IsUsable(requestedName))
        {
            // Which of the three it was, in the log, because they do not mean the same thing to
            // whoever reads it: a dashboard too old to send a name at all is a reminder to
            // update, and a name full of control characters is somebody trying to write lines
            // into this machine's log. The rejected string itself is NOT recorded - writing it
            // down is exactly what the rule refuses.
            string reason = string.IsNullOrEmpty(requestedName)
                ? "it named no process"
                : requestedName.Length > ProcessNameRule.MaxLength
                    ? "the name it carried is too long"
                    : "the name it carried contains control characters";

            LogKillNameUnusable(logger, pid, reason, origin.Reason);

            return Results.Problem(
                detail: "a kill must name the process it means, as ?name=<process name>",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Null only if the system refused before the name could be read.
        string? name = null;

        try
        {
            using Process process = Process.GetProcessById(pid);

            // The name is read BEFORE terminating: afterwards the process has no name left to
            // give, and the logger would keep only a number.
            name = process.ProcessName;

            // Ordinal, and the two sides are the same source: the list was built from
            // Process.ProcessName too, so whatever the platform truncates or capitalises there
            // it truncates and capitalises here. What remains after this check is the few
            // microseconds between reading the name and the call below - it NARROWS the window,
            // it does not close it. What it removes is the second or more during which that
            // list sat on screen, which is where the whole risk was.
            if (!string.Equals(name, requestedName, StringComparison.Ordinal))
            {
                LogKillTargetChanged(logger, pid, name, requestedName, origin.Reason);

                return Results.Problem(
                    detail: FormattableString.Invariant(
                        $"pid {pid} is now {name}, not the {requestedName} this request meant"),
                    statusCode: StatusCodes.Status409Conflict);
            }

            process.Kill();
        }
        catch (ArgumentException)
        {
            LogProcessNotFound(logger, pid, origin.Reason);

            return Results.NotFound();
        }
        catch (InvalidOperationException)
        {
            LogProcessAlreadyExited(logger, pid, origin.Reason);

            return Results.NotFound();
        }
        catch (Win32Exception error)
        {
            // Protected processes are refused by the operating system, even to LocalSystem.
            // There is no list of untouchables of our own to keep up to date: there is the
            // system's refusal, reported for what it is.
            LogKillRefusedBySystem(logger, name, pid, origin.Reason, error.Message);

            return Results.Problem(
                detail: "the operating system refused to terminate this process",
                statusCode: StatusCodes.Status403Forbidden);
        }

        LogProcessTerminated(logger, name, pid, origin.Reason);

        return Results.NoContent();
    }

    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Warning,
        Message = "Process terminated: {Name} (pid {Pid}), requested by {Origin}.")]
    private static partial void LogProcessTerminated(
        ILogger logger, string name, int pid, string origin);

    [LoggerMessage(
        EventId = 11,
        Level = LogLevel.Information,
        Message = "Kill refused: no process with pid {Pid} ({Origin}).")]
    private static partial void LogProcessNotFound(ILogger logger, int pid, string origin);

    [LoggerMessage(
        EventId = 12,
        Level = LogLevel.Information,
        Message = "Kill refused: process {Pid} had already exited ({Origin}).")]
    private static partial void LogProcessAlreadyExited(ILogger logger, int pid, string origin);

    [LoggerMessage(
        EventId = 13,
        Level = LogLevel.Warning,
        Message = "Kill refused by the operating system: {Name} (pid {Pid}), requested by {Origin}: {Error}")]
    private static partial void LogKillRefusedBySystem(
        ILogger logger, string? name, int pid, string origin, string error);

    [LoggerMessage(
        EventId = 14,
        Level = LogLevel.Warning,
        Message = "Kill refused for pid {Pid}: {Reason}, requested by {Origin}.")]
    private static partial void LogKillNameUnusable(
        ILogger logger, int pid, string reason, string origin);

    [LoggerMessage(
        EventId = 15,
        Level = LogLevel.Warning,
        Message = "Kill refused: pid {Pid} is now {Name}, not the {RequestedName} it was asked for, requested by {Origin}.")]
    private static partial void LogKillTargetChanged(
        ILogger logger, int pid, string name, string requestedName, string origin);
}
