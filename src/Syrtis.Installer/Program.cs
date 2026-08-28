using System.Windows.Forms;

namespace Syrtis.Installer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var silent = args.Any(a => a.Equals("--silent", StringComparison.OrdinalIgnoreCase));
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

        var result = SetupRunner.Run(setupPath, SetupRunner.DefaultInstallDirectory, SetupRunner.DefaultLogPath);
        return result.ExitCode;
    }

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
        var candidate = args.FirstOrDefault(a => !a.Equals("--silent", StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(candidate)
            ? Strings.ErrorNoSetupPathArg
            : Strings.ErrorSetupNotFound(candidate);
    }
}
