using System.ComponentModel;
using System.Diagnostics;

using Observer.Core.Processes;
using Observer.Service.LocalChannel;

namespace Observer.Service;

/// <summary>Come una riga di process viaggia sul filo.</summary>
/// <param name="Pid">Identificatore del process.</param>
/// <param name="Name">Name dell'eseguibile.</param>
/// <param name="CpuPercent">
/// Percentuale sull'intera macchina, oppure null quando non e' ancora nota. Null e non zero:
/// il client deve poter mostrare un trattino invece di affermare che il process e' fermo.
/// </param>
/// <param name="WorkingSetBytes">Memoria fisica occupata.</param>
/// <param name="IoBytesPerSecond">
/// Byte al secondo letti e scritti, oppure null quando non e' noto: primo giro, process appena
/// nato, o un sistema che non lo dice - su Linux, i processes degli altri utenti.
/// </param>
public sealed record ProcessRow(
    int Pid, string Name, double? CpuPercent, long WorkingSetBytes, double? IoBytesPerSecond);

/// <summary>La risposta di <c>/processes</c>.</summary>
/// <param name="CapturedAt">Quando e' stato letto l'elenco.</param>
/// <param name="By">
/// Il criterion applicato: <c>cpu</c>, <c>memory</c> o <c>io</c>. Ripetuto apposta: un client
/// che chiede un criterion a un servizio piu' vecchio che non lo conosce riceverebbe l'elenco
/// della CPU, e senza questo campo lo mostrerebbe sotto il titolo sbagliato.
/// </param>
/// <param name="Processes">I processes, gia' sorted.</param>
public sealed record ProcessListResponse(
    DateTimeOffset CapturedAt, string By, IReadOnlyList<ProcessRow> Processes);

/// <summary>Gli endpoint che dicono chi sta consumando la macchina, e permettono di fermarlo.</summary>
/// <remarks>
/// <b>Terminare un process e' l'unica cosa che questo servizio fa e non e' una lettura.</b>
/// Fino a qui Observer esponeva telemetria: un token rubato faceva vedere la CPU altrui. Con
/// questo endpoint lo stesso token ferma processes su quella macchina, e il servizio gira come
/// LocalSystem. La portata resta <c>Anywhere</c> per scelta esplicita del proprietario del
/// progetto, non per omissione — la restrizione al solo canale locale sarebbe una riga sola, e
/// la conseguenza di non metterla e' che il token vale molto di piu' di prima.
/// <para>
/// Per questo ogni tentativo viene registrato con PID, name e provenienza del chiamante, sia
/// quando riesce sia quando il sistema lo rifiuta: un'azione che distrugge stato deve lasciare
/// una traccia, e senza sarebbe l'unica cosa irreversibile del progetto a non averne.
/// </para>
/// </remarks>
public static partial class ProcessEndpoints
{
    /// <summary>Quanti processes si restituiscono quando la richiesta non lo dice.</summary>
    private const int DefaultTop = 15;

    /// <summary>Il massimo restituibile, per non spedire l'intera tabella dei processes.</summary>
    private const int MaxTop = 100;

    /// <summary>Mappa /processes e /processes/{pid}/kill.</summary>
    /// <param name="endpoints">Il costruttore di rotte dell'applicazione.</param>
    public static void MapProcessEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/processes", (ProcessRanking ranking, string? by, int? top) =>
            ListProcesses(ranking, by, top));

        endpoints.MapPost("/processes/{pid:int}/kill", (
            HttpContext context,
            ILoggerFactory loggerFactory,
            int pid) => Terminate(context, loggerFactory, pid));
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

        // Per memoria, per I/O oppure per CPU. Chi non dice niente ottiene la CPU, che e' la
        // domanda che ci si fa guardando un quadrante rosso.
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

    private static IResult Terminate(HttpContext context, ILoggerFactory loggerFactory, int pid)
    {
        ILogger logger = loggerFactory.CreateLogger(typeof(ProcessEndpoints).FullName!);
        CallerOrigin origin = LocalCaller.Classify(context);

        string name;

        try
        {
            using Process process = Process.GetProcessById(pid);

            // Il name si legge PRIMA di terminare: dopo, il process non ha piu' un name da
            // dare, e il logger conserverebbe soltanto un numero.
            name = process.ProcessName;
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
            // I processes protetti li rifiuta il sistema operativo, anche a LocalSystem. Non
            // c'e' un elenco nostro di intoccabili da tenere aggiornato: c'e' il rifiuto del
            // sistema, riportato per quello che e'.
            LogKillRefusedBySystem(logger, pid, origin.Reason, error.Message);

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
        Message = "Kill refused by the operating system: pid {Pid} ({Origin}): {Error}")]
    private static partial void LogKillRefusedBySystem(
        ILogger logger, int pid, string origin, string error);
}
