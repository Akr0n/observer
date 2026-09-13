namespace Observer.Service;

/// <summary>
/// Lets a message through when the reason CHANGES, and then no more than one every
/// <see cref="RepeatInterval"/>, counting the ones kept quiet.
/// </summary>
/// <remarks>
/// A message inside a loop that runs once a second is not a message: it is
/// 86 400 lines a day. On Windows they end up in the <i>Application</i> event log, which
/// by default is twenty megabytes and overwrites the oldest, so a single fault that
/// repeats evicts in a few hours the event history of the WHOLE machine — including
/// the part that would be needed to understand what happened. And if the fault is "disk
/// full", the log that reports it consumes disk.
/// <para>
/// <b>Comparing the reason alone is not enough</b>, and it is the trap that made this class
/// be rewritten: a fault that FLICKERS — a collector wavering around its deadline, a file
/// grabbed intermittently by an antivirus — alternates fault and success at every round,
/// and with the rule "write when it changes" alone every return of the fault is a new
/// reason. Half the flood was left, and alternation is the most likely shape of the
/// faults that get braked here. That is why what counts is the last reason <i>written</i>,
/// which survives the clearing, plus a time window past which a fault that lasts makes
/// itself heard again — with updated numbers, which would otherwise stay those of the
/// first round.
/// </para>
/// <para>
/// The reason must be a STABLE key — the exception's type, not its message; "long", not
/// the round's milliseconds — otherwise it changes at every repetition and brakes
/// nothing.
/// </para>
/// <para>
/// It is not synchronized. Not because "it runs on a single thread" — the sources' throttles
/// are handed to tasks that <see cref="Task.WhenAll(System.Collections.Generic.IEnumerable{Task})"/>
/// runs together — but because every throttle is touched by ONE source only, and the next
/// round starts only after the previous one has been awaited: it is that await that acts
/// as a barrier between the thread that writes on round N and the one that reads on round N+1.
/// Anyone who one day stopped awaiting the previous round so as not to lose ticks — which is
/// exactly the remedy that would come to mind reading the warning about a round that took too
/// long — would open a race here.
/// </para>
/// </remarks>
public sealed class LogThrottle
{
    /// <summary>How often a fault that lasts gets written again.</summary>
    /// <remarks>
    /// Five minutes: rare enough not to fill anything up (288 lines a day in the worst
    /// case, against 86 400), frequent enough to show in a log that the fault is still
    /// going on, and to update the numbers the message carries with it.
    /// </remarks>
    public static readonly TimeSpan RepeatInterval = TimeSpan.FromMinutes(5);

    private readonly TimeProvider clock;
    private readonly TimeSpan repeatInterval;

    private string? ongoingReason;
    private string? lastLoggedReason;
    private long lastLoggedTimestamp;
    private bool ongoingWasLogged;
    private int silenced;

    /// <summary>Creates a throttle.</summary>
    /// <param name="timeProvider">The clock, or null for the system one.</param>
    /// <param name="repeatInterval">How often to repeat, or null for <see cref="RepeatInterval"/>.</param>
    public LogThrottle(TimeProvider? timeProvider = null, TimeSpan? repeatInterval = null)
    {
        clock = timeProvider ?? TimeProvider.System;
        this.repeatInterval = repeatInterval ?? RepeatInterval;
    }

    /// <summary>The condition is here now.</summary>
    /// <param name="reason">A stable key: it changes only if the nature of the fault changes.</param>
    /// <returns><c>true</c> if the message is to be written now.</returns>
    public bool ShouldLog(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);

        ongoingReason = reason;

        bool reasonChanged = !string.Equals(lastLoggedReason, reason, StringComparison.Ordinal);
        bool dueAgain = lastLoggedReason is not null && clock.GetElapsedTime(lastLoggedTimestamp) >= repeatInterval;

        if (!reasonChanged && !dueAgain)
        {
            silenced++;

            return false;
        }

        lastLoggedReason = reason;
        lastLoggedTimestamp = clock.GetTimestamp();
        ongoingWasLogged = true;
        silenced = 0;

        return true;
    }

    /// <summary>The condition is no longer here.</summary>
    /// <param name="silenced">How many times it repeated without anyone writing it.</param>
    /// <returns><c>true</c> if the recovery is to be announced.</returns>
    /// <remarks>
    /// The recovery must be said: a fault that stops is information just as much as a fault
    /// that starts, and without this line the log would show an error and then nothing, which
    /// reads as "it is still happening". It is announced even when the fault lasted a single
    /// round — that is the most common case, and it is precisely the one in which a single
    /// line would let you believe a fault is still open. What is not announced is a fault
    /// nobody wrote, because it was kept quiet inside the window: that would be the end of a
    /// story the log never began, and it is what keeps a flickering fault silent.
    /// </remarks>
    public bool ShouldLogRecovery(out int silenced)
    {
        silenced = this.silenced;

        bool shouldAnnounce = ongoingReason is not null && ongoingWasLogged;

        ongoingReason = null;
        ongoingWasLogged = false;

        return shouldAnnounce;
    }
}
