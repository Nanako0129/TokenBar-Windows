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

    /// <summary>A log path belonging to this run and no other.
    ///
    /// <para>It used to be a fixed <c>syrtis-install.log</c>, and two separate
    /// defects came out of that one name. The first was staleness: a leftover
    /// from an earlier install passes an existence check, so a failure could be
    /// explained with an unrelated — possibly successful — run's record. That
    /// was answered with a last-write comparison against the launch instant,
    /// which was the wrong shape of answer. The second showed why: two wrapper
    /// instances can overlap, a double-click while an automated
    /// <c>--silent</c> run is in flight, and then both children write the same
    /// file. A write by either satisfies the other's timestamp test, because a
    /// timestamp establishes "written after I started" and never "written by my
    /// child".</para>
    ///
    /// <para>No inference distinguishes those. A name that cannot collide
    /// does, by construction, and it makes the question unaskable rather than
    /// answerable — which is the same move as parsing the request once instead
    /// of deriving it in five places.</para>
    ///
    /// <para>Timestamp and process id lead so that someone hunting the file in
    /// %TEMP% can recognise it, but they are not what makes it unique: the
    /// first version stopped there, and the test written to pin it failed
    /// immediately, because two calls in the same second from one process
    /// produce the same name. Cross-instance uniqueness would have survived on
    /// the process id alone — which is an argument, and the point of this
    /// method is not to need one. The random tail settles it outright.</para>
    ///
    /// <para><see cref="LogWrittenByThisRun"/> stays, and is now belt to this
    /// braces: uniqueness settles ownership, the timestamp still answers
    /// whether Setup got far enough to write anything at all.</para></summary>
    internal static string NewLogPath() =>
        Path.Combine(
            Path.GetTempPath(),
            $"syrtis-install-{DateTime.UtcNow:yyyyMMdd-HHmmss}"
                + $"-{Process.GetCurrentProcess().Id}"
                + $"-{Guid.NewGuid():N}".Substring(0, 9)
                + ".log");

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
    internal static string Quote(string value)
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
    /// it was. Setup can start and fail before it ever opens the log, and an
    /// existence check cannot see that — the test machine had a two-day-old
    /// copy at the fixed path this used to use, so the Done page would have
    /// offered an unrelated run's record as the explanation for a failure.
    /// <see cref="NewLogPath"/> now removes the ownership half of that
    /// question; this answers the remaining half, which is whether Setup wrote
    /// anything at all.</para>
    ///
    /// <para>Deleting the old file before launching was the alternative. This
    /// does not touch the user's disk and has no failure mode of its own: if
    /// the stamp cannot be read at all, the answer is "no log", which is the
    /// safe direction.</para></summary>
    internal static string? LogWrittenByThisRun(string logPath, DateTime startedUtc)
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
