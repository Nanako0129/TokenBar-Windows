using System.Diagnostics;

namespace Syrtis.Installer;

/// <summary>Result of one Setup.exe invocation.</summary>
internal readonly struct SetupRunResult(int exitCode, string logPath, string? launchError = null)
{
    internal int ExitCode { get; } = exitCode;
    internal string LogPath { get; } = logPath;

    /// <summary>Why Setup could not be <em>started</em>, or null when it ran.
    /// The distinction matters to what the Done page may say: when Setup never
    /// started it also never opened the log, so pointing the user at that path
    /// shows them either nothing or — because the name is fixed — an unrelated
    /// run from days ago.</summary>
    internal string? LaunchError { get; } = launchError;
}

/// <summary>Invokes Velopack's Setup.exe per-user and silently against a
/// chosen install directory, and reports back its exit code. Shared by both
/// the GUI Installing page (run on a background thread) and `--silent` mode
/// (run on the main thread, no UI ever constructed).</summary>
internal static class SetupRunner
{
    internal static string DefaultInstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nyanako.Syrtis");

    internal static string DefaultLogPath =>
        Path.Combine(Path.GetTempPath(), "syrtis-install.log");

    /// <summary>Wrap one value for a Windows command line.
    ///
    /// <para>CommandLineToArgvW reads <c>\"</c> as a literal quote, so a value
    /// ending in a backslash does not close its own quotes — it swallows
    /// everything after it. <c>C:\</c> is exactly what FolderBrowserDialog
    /// returns for a drive root, and typing a trailing separator is ordinary,
    /// so this was reachable rather than theoretical. Measured on the x64 host
    /// before the fix:</para>
    /// <code>
    /// --installto "C:\" --silent --log "C:\Temp\syrtis-install.log"
    ///   argv[0] = [--installto]
    ///   argv[1] = [C:" --silent --log C:\Temp\syrtis-install.log]
    /// </code>
    /// <para>Two arguments, no <c>--silent</c> and no <c>--log</c> — so Setup
    /// would have run <b>visibly</b>, popping its own window behind a wizard
    /// stuck on the Installing page with every button disabled and the close
    /// box intercepted.</para>
    ///
    /// <para>Only the run of backslashes immediately before the closing quote
    /// is doubled. Interior ones are literal to the parser and doubling them
    /// would corrupt the path.</para></summary>
    private static string Quote(string value)
    {
        var trailing = 0;
        while (trailing < value.Length && value[value.Length - 1 - trailing] == '\\')
        {
            trailing++;
        }

        return string.Concat("\"", value, new string('\\', trailing), "\"");
    }

    internal static SetupRunResult Run(string setupExePath, string installDir, string logPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = setupExePath,
            Arguments = $"--installto {Quote(installDir)} --silent --log {Quote(logPath)}",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Setup.exe.");
        process.WaitForExit();
        return new SetupRunResult(process.ExitCode, logPath);
    }
}
