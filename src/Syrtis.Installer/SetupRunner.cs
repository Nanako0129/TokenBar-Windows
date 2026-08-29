using System.Diagnostics;

namespace Syrtis.Installer;

/// <summary>Result of one Setup.exe invocation.</summary>
internal readonly struct SetupRunResult(int exitCode, string? logPath, string? launchError = null)
{
    internal int ExitCode { get; } = exitCode;

    /// <summary>The log this run produced, or null when it produced none.
    /// Never merely "the path we asked Setup to use" — see
    /// <see cref="SetupRunner.LogWrittenByThisRun"/>.</summary>
    internal string? LogPath { get; } = logPath;

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

        // Read the clock before starting, not after: anything Setup writes to
        // the log necessarily happens later, so ">= startedUtc" is exactly the
        // set of writes this run caused. DateTime.UtcNow is coarse (about 15
        // ms) but it rounds down, so the reading can only be at or before the
        // true launch instant, which keeps the comparison safe without a
        // fudge factor.
        var startedUtc = DateTime.UtcNow;
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Setup.exe.");
        process.WaitForExit();
        return new SetupRunResult(process.ExitCode, LogWrittenByThisRun(logPath, startedUtc));
    }

    /// <summary>The log path when this run wrote it, null otherwise.
    ///
    /// <para>An existence check is not enough and was shipped once believing
    /// it was. <see cref="DefaultLogPath"/> is a fixed name in %TEMP%, so a
    /// file being there says nothing about which install put it there — the
    /// test machine had a two-day-old copy at exactly that path. Setup can
    /// start and fail before opening the log, and the Done page would then
    /// have offered an unrelated, possibly successful, install's record as the
    /// explanation for this failure.</para>
    ///
    /// <para>Deleting the old file before launching was the alternative. This
    /// does not touch the user's disk and has no failure mode of its own: if
    /// the stamp cannot be read at all, the answer is "no log", which is the
    /// safe direction.</para></summary>
    private static string? LogWrittenByThisRun(string logPath, DateTime startedUtc)
    {
        try
        {
            var info = new FileInfo(logPath);
            return info.Exists && info.LastWriteTimeUtc >= startedUtc ? logPath : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
