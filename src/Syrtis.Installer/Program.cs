using System.Windows.Forms;

namespace Syrtis.Installer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var request = InstallRequest.Parse(args);
        return request.Kind switch
        {
            InstallRequestKind.SelfCheck => SelfCheck.Run(),
            InstallRequestKind.Refuse => Fail(request),
            InstallRequestKind.RunSilent => RunSilent(request),
            InstallRequestKind.RunWizard => RunGui(request),
            _ => throw new InvalidOperationException($"Unknown request kind: {request.Kind}"),
        };
    }

    /// <summary>Report a startup problem on whichever surface exists, and
    /// exit 1. Interactive gets a dialog; anything else gets stderr, which is
    /// subject to the console limitation documented on RunSilent.</summary>
    private static int Fail(InstallRequest request)
    {
        var message = MissingSetupMessage(request);
        if (!request.SilentRequested && Environment.UserInteractive)
        {
            MessageBox.Show(message, Strings.WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        else
        {
            Console.Error.WriteLine(message);
        }

        return ExitCodes.BadArguments;
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
    private static int RunSilent(InstallRequest request)
    {
        var setupPath = request.SetupPath!;

        // Locate only proved the file is there. Windows can still refuse to
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
            // LaunchFailed rather than BadArguments: BadArguments already means
            // "we never got as far as running it", and Setup's own codes are
            // small positives, so a caller can tell "Setup failed" from "Setup
            // never started". Matches the code the GUI path records for a
            // faulted launch.
            return ExitCodes.LaunchFailed;
        }
    }

    private static int RunGui(InstallRequest request)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new WizardForm(request.SetupPath!));
        return ExitCodes.Ok;
    }

    /// <summary>Renders a Refuse request's reason + token pair into the
    /// sentence a person reads. Kept separate from InstallRequest itself so
    /// that parsing never touches Strings, and so a test can assert the
    /// reason/token pair without going through culture-dependent text.</summary>
    private static string MissingSetupMessage(InstallRequest request) => request.Reason switch
    {
        RefusalReason.NoSetupPath => Strings.ErrorNoSetupPathArg,
        RefusalReason.SetupNotFound => Strings.ErrorSetupNotFound(request.Token!),
        RefusalReason.UnexpectedArgument => Strings.ErrorUnexpectedArg(request.Token!),
        _ => throw new InvalidOperationException($"Unknown refusal reason: {request.Reason}"),
    };
}
