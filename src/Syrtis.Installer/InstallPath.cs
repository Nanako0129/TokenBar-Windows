namespace Syrtis.Installer;

/// <summary>Validation for a per-user install directory typed on the Location
/// page. Moved out of WizardForm unchanged so it can be exercised by
/// --self-check on the framework that actually ships (net48's Path APIs throw
/// on invalid characters where .NET Core's do not — see the comment inside
/// IsValidInstallPath). Not part of TokenBar.Core.Tests: that suite is net10
/// and also runs on macOS, where this method's Windows path semantics do not
/// hold and would be green for the wrong reason or red in the inner loop.</summary>
internal static class InstallPath
{
    /// <summary>A "usable absolute directory path": rooted, and syntactically
    /// well-formed (Path.GetFullPath does not throw). Whether it can actually
    /// be created is left to Setup.exe, which reports failure through its
    /// exit code on the Done page — this is user-typing feedback, not a
    /// filesystem permission check.</summary>
    internal static bool IsValidInstallPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        // EVERY System.IO.Path call belongs inside this try. On .NET Framework
        // — measured, not assumed, on 4.8.9337.0 — IsPathRooted and GetPathRoot
        // both call CheckInvalidPathChars and throw ArgumentException, so a
        // single quote in C:\bad" throws from all three of these. This runs
        // from TextChanged on the UI thread, so one outside the guard is an
        // unhandled exception on a keystroke rather than the invalid-path hint.
        // .NET Core stopped throwing from these; that is not the target here.
        try
        {
            if (!Path.IsPathRooted(path))
            {
                return false;
            }

            // Rooted is not the same as fully qualified, and there is more than
            // one way to be rooted without naming a volume. .NET Framework has
            // no Path.IsPathFullyQualified (that arrived in .NET Core 2.1), so
            // the test is on the root, and it is a whitelist: the root must
            // identify a volume. An earlier revision listed the bad shapes
            // instead and shipped with the second one still open.
            //
            // Measured on 4.8.9337.0, process directory C:\Users\Nanako:
            //
            //   "C:\ok"          root "C:\"          -> qualified
            //   "\\srv\share\x"  root "\\srv\share"  -> qualified
            //   "C:"             root "C:"           -> C:\Users\Nanako
            //   "\Syrtis"        root "\"            -> C:\Syrtis
            //   "/Syrtis"        root "\"            -> C:\Syrtis
            //
            // The last three are the trap: every one of them passes
            // IsPathRooted and resolves silently against the process's current
            // drive or directory, so the user reads an absolute path and Setup
            // installs somewhere they never named. GetPathRoot normalises "//"
            // to "\\", so the UNC test needs only the one form.
            var root = Path.GetPathRoot(path);
            var namesAVolume =
                (root.Length >= 3 && root[1] == ':')
                || root.StartsWith(@"\\", StringComparison.Ordinal);
            if (!namesAVolume)
            {
                return false;
            }

            Path.GetFullPath(path);
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (PathTooLongException) { return false; }
    }
}
