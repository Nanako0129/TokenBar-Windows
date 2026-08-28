namespace Syrtis.Installer;

/// <summary>Where Velopack's Setup.exe comes from. Slice 1a takes it as a
/// command-line argument; slice 1b will embed it in the wizard exe instead.
/// Keeping the lookup in this one method is the seam 1b swaps.</summary>
internal static class SetupLocator
{
    /// <summary>Resolves the path to Setup.exe from the process arguments, or
    /// null if none was given / the given path does not exist.</summary>
    internal static string? Locate(string[] args)
    {
        var path = args.FirstOrDefault(a => !a.Equals("--silent", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        return Path.GetFullPath(path);
    }
}
