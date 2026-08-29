using System.ComponentModel;
using System.Windows.Forms;

namespace Syrtis.Installer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var silent = args.Any(SetupLocator.IsSilentSwitch);
        return silent ? RunSilent(args) : RunGui(args);
    }

    /// <summary>No window at all: runs Setup against the default per-user
    /// directory and propagates its exit code. Must not construct any UI
    /// type, so this path never touches System.Windows.Forms.</summary>
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
        //
        // Win32Exception lives in System.dll, not System.Windows.Forms.dll, so
        // catching it here does not put a UI type on this path.
        try
        {
            var result = SetupRunner.Run(
                setupPath, SetupRunner.DefaultInstallDirectory, SetupRunner.DefaultLogPath);
            return result.ExitCode;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
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
