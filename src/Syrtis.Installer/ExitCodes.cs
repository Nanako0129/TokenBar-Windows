namespace Syrtis.Installer;

/// <summary>Every exit code this process returns, named at one place instead
/// of scattered as magic numbers across Program and WizardForm. Moved here
/// from <c>Program.LaunchFailedExitCode</c> so WizardForm no longer needs a
/// reference to Program just to report a launch failure.</summary>
internal static class ExitCodes
{
    /// <summary>Ran to completion — Setup's own exit code (RunSilent, or
    /// --self-check with every assertion passing) or a wizard closed
    /// normally.</summary>
    internal const int Ok = 0;

    /// <summary>The arguments were rejected before anything ran: no setup
    /// path, the setup file was not found, an unexpected extra argument, or
    /// --self-check found a broken assertion.</summary>
    internal const int BadArguments = 1;

    /// <summary>Setup.exe could not be started at all, as opposed to starting
    /// and failing. Kept away from 1 (bad or missing argument) and from
    /// Setup's own small positive codes.</summary>
    internal const int LaunchFailed = -1;
}
