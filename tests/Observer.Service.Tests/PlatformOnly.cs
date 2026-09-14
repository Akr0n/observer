namespace Observer.Service.Tests;

/// <summary>A fact that is SKIPPED outside Windows instead of failing.</summary>
/// <remarks>
/// xunit 2.9.3 has no Assert.Skip: the only way to skip by platform is to set Skip in the
/// attribute's constructor. Skipping is the right outcome, not giving up: a named pipe test
/// that failed on ubuntu-latest would turn the wrong runner red and hide the real faults.
/// </remarks>
public sealed class WindowsOnlyAttribute : FactAttribute
{
    /// <summary>Skips when the system is not Windows.</summary>
    public WindowsOnlyAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Named pipe and Windows identity: run only on windows-latest.";
        }
    }
}

/// <summary>A fact that is SKIPPED outside Linux instead of failing.</summary>
public sealed class LinuxOnlyAttribute : FactAttribute
{
    /// <summary>Skips when the system is not Linux.</summary>
    public LinuxOnlyAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "SO_PEERCRED exists only on Linux: run only on ubuntu-latest.";
        }
    }
}