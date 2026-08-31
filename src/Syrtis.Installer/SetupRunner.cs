using System.Diagnostics;

namespace Syrtis.Installer;

/// <summary>Result of one Setup.exe invocation.</summary>
internal readonly struct SetupRunResult(int exitCode, string? logPath, string? launchError = null)
{
    internal int ExitCode { get; } = exitCode;

    /// <summary>The log this run produced, or null when it produced none.
    /// Never merely "the path we asked Setup to use" — see
    /// <see cref="SetupRunner.WrittenLog"/>.</summary>
    internal string? LogPath { get; } = logPath;

    /// <summary>Why Setup could not be <em>started</em>, or null when it ran.
    /// The distinction matters to what the Done page may say: when Setup never
    /// started it also never opened the log, so pointing the user at that path
    /// would show them nothing at all.</summary>
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
    /// <para>This is also the <em>only</em> mechanism now. It was briefly
    /// paired with a last-write comparison kept as a second line of defence,
    /// and that was a mistake: once the name cannot collide, the timestamp adds
    /// no information and can only be wrong. See
    /// <see cref="WrittenLog"/>.</para></summary>
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

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Setup.exe.");
        process.WaitForExit();
        return new SetupRunResult(process.ExitCode, WrittenLog(logPath));
    }

    /// <summary>The log this run produced, or null when Setup never got far
    /// enough to open one.
    ///
    /// <para>Existence is the whole test, because <see cref="NewLogPath"/>
    /// hands out a name no other run can hold. That was not always true: with
    /// one fixed filename a leftover passed an existence check, which was
    /// answered here with a last-write comparison against the launch instant,
    /// and then uniqueness was added on top and the comparison kept as a
    /// second line of defence.</para>
    ///
    /// <para>Keeping it was wrong. Against a name that cannot collide the
    /// comparison carries no information, and it can still be false: %TEMP% on
    /// FAT or exFAT records write times to two-second granularity, and the
    /// clock can step backwards mid-install. Either makes a log this run really
    /// did write appear older than the moment the run began, and the Done page
    /// would then report that no log exists — hiding the one artifact that
    /// explains the failure, at the moment it is wanted. A redundant check that
    /// can produce a false negative is not a second line of defence, it is a
    /// second way to be wrong.</para></summary>
    internal static string? WrittenLog(string logPath)
    {
        try
        {
            return File.Exists(logPath) ? logPath : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
