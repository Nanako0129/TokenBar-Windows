using System.Diagnostics;

namespace Syrtis.Installer;

/// <summary>Result of one Setup.exe invocation.</summary>
internal readonly struct SetupRunResult(int exitCode, string logPath)
{
    internal int ExitCode { get; } = exitCode;
    internal string LogPath { get; } = logPath;
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

    internal static SetupRunResult Run(string setupExePath, string installDir, string logPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = setupExePath,
            Arguments = $"--installto \"{installDir}\" --silent --log \"{logPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Setup.exe.");
        process.WaitForExit();
        return new SetupRunResult(process.ExitCode, logPath);
    }
}
