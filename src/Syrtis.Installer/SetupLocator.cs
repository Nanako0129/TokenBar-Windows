namespace Syrtis.Installer;

/// <summary>Where Velopack's Setup.exe comes from. Slice 1a takes it as a
/// command-line argument; slice 1b will embed it in the wizard exe instead.
/// Keeping the lookup in this one method is the seam 1b swaps.</summary>
internal static class SetupLocator
{
    /// <summary>Whether an argument is the silent switch, in either of the two
    /// forms Velopack's own Setup.exe documents (<c>-s, --silent</c> in its
    /// --help output).
    ///
    /// <para>This lives here, and both this class and Program call it, because
    /// the test was previously written out twice against the same literal and
    /// the two copies disagreed the moment <c>-s</c> was added: Program would
    /// have taken the interactive path while Locate treated <c>-s</c> as the
    /// setup file. Slice 1b publishes this wrapper under Setup.exe's own
    /// filename, so a script that already says <c>Setup.exe -s</c> has to keep
    /// working.</para></summary>
    internal static bool IsSilentSwitch(string arg) =>
        arg.Equals("--silent", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("-s", StringComparison.OrdinalIgnoreCase);

    /// <summary>Resolves the path to Setup.exe from the process arguments, or
    /// null if none was given / the given path does not exist.</summary>
    internal static string? Locate(string[] args)
    {
        var path = args.FirstOrDefault(a => !IsSilentSwitch(a));
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        return Path.GetFullPath(path);
    }
}
