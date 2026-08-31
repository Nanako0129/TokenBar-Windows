namespace Syrtis.Installer;

/// <summary>Runs on the framework that actually ships, via the hidden
/// --self-check switch, so InstallPath's net48-only behaviour (Path APIs
/// throwing on invalid characters, where .NET Core's do not) is pinned
/// somewhere runnable — TokenBar.Core.Tests is net10 and also runs on macOS,
/// so it cannot see this. Not wired into CI yet; runnable by hand on the
/// Windows host. Deliberately not testable from Core.Tests, and deliberately
/// not pulled into it — see InstallPath's own remarks.</summary>
internal static class SelfCheck
{
    internal static int Run()
    {
        var failures = new List<string>();

        void Expect(string label, bool actual, bool expected)
        {
            if (actual != expected)
            {
                failures.Add($"{label}: expected {expected}, got {actual}");
            }
        }

        // Rooted but not fully qualified — resolves silently against the
        // process's current drive/directory (finding: the whitelist trap
        // documented on InstallPath.IsValidInstallPath). Must be rejected.
        Expect("IsValidInstallPath(\"C:\")", InstallPath.IsValidInstallPath("C:"), false);
        Expect("IsValidInstallPath(\"\\Syrtis\")", InstallPath.IsValidInstallPath(@"\Syrtis"), false);

        // Genuinely rooted at a volume. Must be accepted.
        Expect("IsValidInstallPath(\"C:\\ok\")", InstallPath.IsValidInstallPath(@"C:\ok"), true);
        Expect("IsValidInstallPath(\"\\\\srv\\share\\x\")", InstallPath.IsValidInstallPath(@"\\srv\share\x"), true);

        // On .NET Framework, Path.IsPathRooted/GetPathRoot throw
        // ArgumentException for these; IsValidInstallPath must catch and
        // report them as invalid, not let the exception escape.
        Expect("IsValidInstallPath(quote)", InstallPath.IsValidInstallPath("C:\\bad\"path"), false);
        Expect("IsValidInstallPath(pipe)", InstallPath.IsValidInstallPath("C:\\bad|path"), false);

        foreach (var failure in failures)
        {
            Console.Error.WriteLine(failure);
        }

        return failures.Count == 0 ? ExitCodes.Ok : ExitCodes.BadArguments;
    }
}
