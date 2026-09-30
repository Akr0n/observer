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

/// <summary>A theory that is SKIPPED outside Linux instead of failing.</summary>
public sealed class LinuxOnlyTheoryAttribute : TheoryAttribute
{
    /// <summary>Skips when the system is not Linux.</summary>
    public LinuxOnlyTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Files and errors of Linux: run only on ubuntu-latest.";
        }
    }
}

/// <summary>A fact that needs root, and is SKIPPED for anyone else.</summary>
/// <remarks>
/// GitHub's runner is not root, so the ordinary test step skips these, and skipping says so
/// instead of pretending. They are run as root by a step of their own in build.yml, by name,
/// which fails unless they are reported as passed; a test added here has to be added to that
/// step's filter, or it is proved only by hand as root.
/// </remarks>
public sealed class RootOnlyFactAttribute : FactAttribute
{
    /// <summary>Skips unless this is Linux and the process is root.</summary>
    public RootOnlyFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || !Environment.IsPrivilegedProcess)
        {
            Skip = "Needs root: run by its own step in build.yml, and by hand as root.";
        }
    }
}

/// <summary>A fact about an account that is NOT root, SKIPPED for root.</summary>
public sealed class UnprivilegedFactAttribute : FactAttribute
{
    /// <summary>Skips off Linux, and as root.</summary>
    public UnprivilegedFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || Environment.IsPrivilegedProcess)
        {
            Skip = "Root is allowed what this checks is refused: run it as an ordinary account.";
        }
    }
}