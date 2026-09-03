using System.ComponentModel;
using System.Diagnostics;

using Observer.Core.Processes;
using Observer.Service.LocalChannel;

namespace Observer.Service;

/// <summary>Come una riga di processo viaggia sul filo.</summary>
/// <param name="Pid">Identificatore del processo.</param>
/// <param name="Name">Nome dell'eseguibile.</param>
/// <param name="CpuPercent">
/// Percentuale sull'intera macchina, oppure null quando non e' ancora nota. Null e non zero:
/// il client deve poter mostrare un trattino invece di affermare che il processo e' fermo.
/// </param>
/// <param name="WorkingSetBytes">Memoria fisica occupata.</param>
/// <param name="IoBytesPerSecond">
/// Byte al secondo letti e scritti, oppure null quando non e' noto: primo giro, processo appena
/// nato, o un sistema che non lo dice - su Linux, i processi degli altri utenti.
/// </param>
public sealed record ProcessRow(
    int Pid, string Name, double? CpuPercent, long WorkingSetBytes, double? IoBytesPerSecond);

/// <summary>La risposta di <c>/processes</c>.</summary>
/// <param name="CapturedAt">Quando e' stato letto l'elenco.</param>
/// <param name="By">
/// Il criterio applicato: <c>cpu</c>, <c>memory</c> o <c>io</c>. Ripetuto apposta: un client
/// che chiede un criterio a un servizio piu' vecchio che non lo conosce riceverebbe l'elenco
/// della CPU, e senza questo campo lo mostrerebbe sotto il titolo sbagliato.
/// </param>
/// <param name="Processes">I processi, gia' ordinati.</param>
public sealed record ProcessListResponse(
    DateTimeOffset CapturedAt, string By, IReadOnlyList<ProcessRow> Processes);

/// <summary>Gli endpoint che dicono chi sta consumando la macchina, e permettono di fermarlo.</summary>
/// <remarks>
/// <b>Terminare un processo e' l'unica cosa che questo servizio fa e non e' una lettura.</b>
/// Fino a qui Observer esponeva telemetria: un token rubato faceva vedere la CPU altrui. Con
/// questo endpoint lo stesso token ferma processi su quella macchina, e il servizio gira come
/// LocalSystem. La portata resta <c>Ovunque</c> per scelta esplicita del proprietario del
/// progetto, non per omissione — la restrizione al solo canale locale sarebbe una riga sola, e
/// la conseguenza di non metterla e' che il token vale molto di piu' di prima.
/// <para>
/// Per questo ogni tentativo viene registrato con PID, nome e provenienza del chiamante, sia
/// quando riesce sia quando il sistema lo rifiuta: un'azione che distrugge stato deve lasciare
/// una traccia, e senza sarebbe l'unica cosa irreversibile del progetto a non averne.
/// </para>
/// </remarks>
public static partial class ProcessEndpoints
{
    /// <summary>Quanti processi si restituiscono quando la richiesta non lo dice.</summary>
    private const int QuantiPerDefault = 15;

    /// <summary>Il massimo restituibile, per non spedire l'intera tabella dei processi.</summary>
    private const int QuantiAlMassimo = 100;

    /// <summary>Mappa /processes e /processes/{pid}/kill.</summary>
    /// <param name="endpoints">Il costruttore di rotte dell'applicazione.</param>
    public static void MapProcessEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/processes", (ProcessRanking classifica, string? by, int? top) =>
            Elenco(classifica, by, top));

        endpoints.MapPost("/processes/{pid:int}/kill", (
            HttpContext contesto,
            ILoggerFactory registri,
            int pid) => Termina(contesto, registri, pid));
    }

    private static IResult Elenco(ProcessRanking classifica, string? by, int? top)
    {
        if (!classifica.TryLeggi(out IReadOnlyList<ProcessUsage> processi))
        {
            return Results.Problem(
                detail: "the process list could not be read on this machine",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        int quanti = Math.Clamp(top ?? QuantiPerDefault, 1, QuantiAlMassimo);

        // Per memoria, per I/O oppure per CPU. Chi non dice niente ottiene la CPU, che e' la
        // domanda che ci si fa guardando un quadrante rosso.
        string criterio = Criterio(by);

        IReadOnlyList<ProcessUsage> ordinati = criterio switch
        {
            "memory" => ProcessRanking.PiuAffamatiDiMemoria(processi, quanti),
            "io" => ProcessRanking.PiuAffamatiDiIo(processi, quanti),
            _ => ProcessRanking.PiuAffamatiDiCpu(processi, quanti),
        };

        return Results.Ok(new ProcessListResponse(
            DateTimeOffset.UtcNow,
            criterio,
            [.. ordinati.Select(processo => new ProcessRow(
                processo.Pid,
                processo.Name,
                processo.CpuPercent,
                processo.WorkingSet.Bytes,
                processo.IoBytesPerSecond))]));
    }

    private static string Criterio(string? by) => by?.ToUpperInvariant() switch
    {
        "MEMORY" => "memory",
        "IO" => "io",
        _ => "cpu",
    };

    private static IResult Termina(HttpContext contesto, ILoggerFactory registri, int pid)
    {
        ILogger registro = registri.CreateLogger(typeof(ProcessEndpoints).FullName!);
        CallerOrigin origine = LocalCaller.Classifica(contesto);

        string nome;

        try
        {
            using Process processo = Process.GetProcessById(pid);

            // Il nome si legge PRIMA di terminare: dopo, il processo non ha piu' un nome da
            // dare, e il registro conserverebbe soltanto un numero.
            nome = processo.ProcessName;
            processo.Kill();
        }
        catch (ArgumentException)
        {
            LogProcessoAssente(registro, pid, origine.Diagnostica);

            return Results.NotFound();
        }
        catch (InvalidOperationException)
        {
            LogProcessoGiaFinito(registro, pid, origine.Diagnostica);

            return Results.NotFound();
        }
        catch (Win32Exception errore)
        {
            // I processi protetti li rifiuta il sistema operativo, anche a LocalSystem. Non
            // c'e' un elenco nostro di intoccabili da tenere aggiornato: c'e' il rifiuto del
            // sistema, riportato per quello che e'.
            LogRifiutatoDalSistema(registro, pid, origine.Diagnostica, errore.Message);

            return Results.Problem(
                detail: "the operating system refused to terminate this process",
                statusCode: StatusCodes.Status403Forbidden);
        }

        LogProcessoTerminato(registro, nome, pid, origine.Diagnostica);

        return Results.NoContent();
    }

    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Warning,
        Message = "Process terminated: {Nome} (pid {Pid}), requested by {Origine}.")]
    private static partial void LogProcessoTerminato(
        ILogger logger, string nome, int pid, string origine);

    [LoggerMessage(
        EventId = 11,
        Level = LogLevel.Information,
        Message = "Kill refused: no process with pid {Pid} ({Origine}).")]
    private static partial void LogProcessoAssente(ILogger logger, int pid, string origine);

    [LoggerMessage(
        EventId = 12,
        Level = LogLevel.Information,
        Message = "Kill refused: process {Pid} had already exited ({Origine}).")]
    private static partial void LogProcessoGiaFinito(ILogger logger, int pid, string origine);

    [LoggerMessage(
        EventId = 13,
        Level = LogLevel.Warning,
        Message = "Kill refused by the operating system: pid {Pid} ({Origine}): {Errore}")]
    private static partial void LogRifiutatoDalSistema(
        ILogger logger, int pid, string origine, string errore);
}
