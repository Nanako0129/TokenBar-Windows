namespace TokenBar.App;

/// <summary>Append-only dev log for remote (SSH) verification, where nothing
/// else surfaces startup failures. Cheap and always on for now; becomes a
/// proper logger when the shell grows up.</summary>
public static class DevLog
{
    private static readonly string PathName =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tokenbar-app.log");

    public static void Write(string message)
    {
        try
        {
            System.IO.File.AppendAllText(
                PathName, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
