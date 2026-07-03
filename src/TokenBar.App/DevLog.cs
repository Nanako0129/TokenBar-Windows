namespace TokenBar.App;

/// <summary>Append-only dev log for remote (SSH) verification, where nothing
/// else surfaces startup failures. Cheap and always on for now; becomes a
/// proper logger when the shell grows up.</summary>
public static class DevLog
{
    private static readonly string PathName =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tokenbar-app.log");

    // AppendAllText opens the file exclusively; the dashboard lanes write
    // concurrently, and a collision would silently drop the losing line.
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            // InvariantCulture: in a custom time format ':' is the culture's
            // time-separator placeholder, so some locales would render '.' here.
            var ts = DateTime.Now.ToString(
                "HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
            lock (Gate)
            {
                System.IO.File.AppendAllText(
                    PathName, $"{ts} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
