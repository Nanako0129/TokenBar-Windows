using System.Windows.Forms;

namespace Syrtis.Installer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Reject what we do not understand before doing anything with it. The
        // wizard used to take the first non-switch argument as the setup path
        // and drop everything else on the floor, so
        // "--silent setup.exe --installto D:\X" installed to the default
        // directory and said nothing at all. Slice 1b publishes this under
        // Setup.exe's own filename, and Setup does support --installto, so a
        // script carrying one would have been silently disobeyed — the worst
        // available outcome for an unattended install.
        var extra = args.Where(a => !SetupLocator.IsSilentSwitch(a)).Skip(1).FirstOrDefault();
        if (extra != null)
        {
            return Fail(Strings.ErrorUnexpectedArg(extra), args);
        }

        var silent = args.Any(SetupLocator.IsSilentSwitch);
        return silent ? RunSilent(args) : RunGui(args);
    }

    /// <summary>Report a startup problem on whichever surface exists, and
    /// exit 1. Interactive gets a dialog; anything else gets stderr, which is
    /// subject to the console limitation documented on RunSilent.</summary>
    private static int Fail(string message, string[] args)
    {
        if (!args.Any(SetupLocator.IsSilentSwitch) && Environment.UserInteractive)
        {
            MessageBox.Show(message, Strings.WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        else
        {
            Console.Error.WriteLine(message);
        }

        return 1;
    }

    /// <summary>No window at all: runs Setup against the default per-user
    /// directory and propagates its exit code. Must not construct any UI
    /// type, so this path never touches System.Windows.Forms.
    ///
    /// <para><b>Known limitation, deferred to slice 1b.</b> This assembly is a
    /// WinExe, so it is a Windows-subsystem process and does not attach the
    /// parent's console. Standard handles are inherited when they are pipes or
    /// files — which is why every measurement of these messages has shown them,
    /// including the ones taken over SSH — but a person typing this in a real
    /// console window gets the exit code and no text. The contract this branch
    /// actually makes is the exit code (1 never reached Setup, -1 could not
    /// start it, anything else is Setup's own), and that holds everywhere; the
    /// text is a convenience that is present exactly when something is
    /// capturing it. Making it unconditional means AttachConsole
    /// (ATTACH_PARENT_PROCESS), which cannot be verified from a remote session
    /// for the same reason the defect cannot be reproduced there, so it waits
    /// for 1b and a real console.</para></summary>
    private static int RunSilent(string[] args)
    {
        var setupPath = SetupLocator.Locate(args);
        if (setupPath == null)
        {
            Console.Error.WriteLine(MissingSetupMessage(args));
            return 1;
        }

        // Locate only proves the file is there. Windows can still refuse to
        // run it — quarantined between the check and here, blocked by policy,
        // permissions changed, or simply not an executable — and Process.Start
        // then throws. Measured: --silent against an existing .txt exited
        // -532462766 (0xE0434352, unhandled managed exception) with a
        // Win32Exception on stderr. That defeats the entire point of this
        // branch, which exists so that scripted installs keep working when the
        // interactive wizard cannot. The GUI path already turns the same
        // failure into its Done page via task.IsFaulted; this is the headless
        // equivalent.
        try
        {
            var result = SetupRunner.Run(
                setupPath, SetupRunner.DefaultInstallDirectory, SetupRunner.DefaultLogPath);
            return result.ExitCode;
        }
        // Catch breadth deliberately matches the GUI path's task.IsFaulted,
        // which catches everything. Naming two exception types here left
        // UnauthorizedAccessException and anything else still exiting
        // -532462766 — the same crash this branch exists to prevent, narrowed
        // rather than removed. The contract is a stable exit code, so nothing
        // Process.Start can raise may escape it.
        catch (Exception ex)
        {
            Console.Error.WriteLine(Strings.ErrorSetupLaunchFailed(setupPath, ex.Message));
            // -1 rather than 1: 1 already means "we never got as far as
            // running it", and Setup's own codes are small positives, so a
            // caller can tell "Setup failed" from "Setup never started".
            // Matches the code the GUI path records for a faulted launch.
            return LaunchFailedExitCode;
        }
    }

    /// <summary>Returned when Setup.exe could not be started at all, as
    /// opposed to starting and failing. Kept away from 1 (bad or missing
    /// argument) and from Setup's own small positive codes.</summary>
    internal const int LaunchFailedExitCode = -1;

    private static int RunGui(string[] args)
    {
        var setupPath = SetupLocator.Locate(args);
        if (setupPath == null)
        {
            var message = MissingSetupMessage(args);
            // MessageBox needs a window station/desktop (Environment.UserInteractive);
            // without one it throws instead of showing anything, which would
            // turn "no argument" into a crash. Console is the fallback there.
            if (Environment.UserInteractive)
            {
                MessageBox.Show(message, Strings.WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else
            {
                Console.Error.WriteLine(message);
            }

            return 1;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new WizardForm(setupPath));
        return 0;
    }

    private static string MissingSetupMessage(string[] args)
    {
        var candidate = args.FirstOrDefault(a => !SetupLocator.IsSilentSwitch(a));
        return string.IsNullOrWhiteSpace(candidate)
            ? Strings.ErrorNoSetupPathArg
            : Strings.ErrorSetupNotFound(candidate);
    }
}
