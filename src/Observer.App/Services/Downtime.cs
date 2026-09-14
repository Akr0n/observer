using System.Globalization;

namespace Observer.App.Services;

/// <summary>
/// How long a fault has lasted, in one short phrase.
/// </summary>
/// <remarks>
/// It lives here and not in the view model for the same reason as <see cref="StatusEscalation"/>
/// and <c>WindowPlacement</c>: it is a rule that can be tested without a window, and a slip here
/// fails silently — it shows a plausible, wrong number next to a machine that is down.
/// <para>
/// The duration is TRUNCATED, not rounded, and that is a choice that keeps even a late text
/// honest. The line refreshes when a reading arrives: every second for the watched machine,
/// every ten when the window is minimized, and every fifteen for the others — twenty when
/// minimized, because the probe fires on the timer's tick — plus the wait for the answer, which
/// runs to eight seconds. The worst case is therefore close to half a minute. A text rounded up
/// would say "3 min" when 2 min and 31 s have passed, whereas by truncating, the number shown
/// stays a LOWER bound on the measured duration: whoever reads "2 min" knows it is at least two
/// minutes.
/// </para>
/// <para>
/// That bound holds against the LAG of the readings, not against a clock that jumps. The
/// measurement comes from two readings of the wall clock, so a step forward — an NTP alignment,
/// a virtual machine resumed from suspension — inflates the duration by its own size, over an
/// interval in which no measurement was taken at all. Measuring from a monotonic source would
/// close that, but it would also touch the probe and history deadlines, which read the same
/// clock: that is a separate decision, not a one-line change.
/// </para>
/// <para>
/// Two units at most, and the second one only if it is not zero: "2 h 10 min" says as much as is
/// needed, "2 h 10 min 33 s" asks you to read three numbers to learn one thing. Seconds never
/// appear, because with readings this far apart they would be a precision the data does not
/// have.
/// </para>
/// </remarks>
public static class Downtime
{
    /// <summary>The duration, as a phrase to put next to the machine's name.</summary>
    /// <param name="duration">How long the fault has lasted.</param>
    /// <returns>For example <c>under 1 min</c>, <c>3 min</c>, <c>2 h 10 min</c>, <c>2 days 3 h</c>.</returns>
    public static string Describe(TimeSpan duration)
    {
        // A negative duration too: the system clock can go backwards between one reading and the
        // next, and "under 1 min" is the only true thing that can be said then.
        if (duration < TimeSpan.FromMinutes(1))
        {
            return "under 1 min";
        }

        if (duration < TimeSpan.FromHours(1))
        {
            return Format(duration.Minutes) + " min";
        }

        if (duration < TimeSpan.FromDays(1))
        {
            return Append(Format((int)duration.TotalHours) + " h", duration.Minutes, " min");
        }

        int days = duration.Days;
        string head = Format(days) + (days == 1 ? " day" : " days");

        return Append(head, duration.Hours, " h");
    }

    private static string Append(string head, int tail, string unit) =>
        tail == 0 ? head : head + " " + Format(tail) + unit;

    // CA1305 is live because InvariantGlobalization is not set: a number with no explicit culture
    // does not compile. Invariant and not current, because this text belongs to the interface,
    // which in Observer is in English.
    private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);
}
