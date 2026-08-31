namespace Syrtis.Installer;

/// <summary>Velopack's Setup.exe, embedded in this assembly as a managed
/// resource by <c>scripts/package-velopack.ps1</c> when it builds the
/// shipping wrapper. A plain `dotnet build` with no payload property (the
/// 1a-style build, and every developer inner-loop build) carries no such
/// resource, which is exactly what <see cref="HasPayload"/> tells apart —
/// see <see cref="InstallRequest.Parse"/>, which takes that as a
/// parameter rather than re-deriving it, for why.</summary>
internal static class EmbeddedPayload
{
    private const string ResourceName = "Syrtis.Installer.payload.setup.exe";

    /// <summary>Whether this build carries the embedded Setup.exe. False for
    /// every net48 build made without the packing script's MSBuild property,
    /// including the entire net10 test assembly — <c>Parse</c> is exercised
    /// against both truth values explicitly rather than trusting this to
    /// vary between test runs.</summary>
    internal static bool HasPayload =>
        typeof(EmbeddedPayload).Assembly.GetManifestResourceInfo(ResourceName) != null;

    /// <summary>Streams the embedded payload out to a fresh, per-run temp
    /// file and returns its path. Never materialises the ~83 MB resource in
    /// memory — copied through a buffer instead, the same way any large file
    /// copy is. Named the way <see cref="SetupRunner.NewLogPath"/> is and for
    /// the same reason: a fixed name can collide between two overlapping
    /// wrapper instances, or survive as a stale leftover from a previous
    /// run.</summary>
    internal static string ExtractToTemp()
    {
        using var resource = typeof(EmbeddedPayload).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded payload resource '{ResourceName}' is missing.");

        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"syrtis-setup-{DateTime.UtcNow:yyyyMMdd-HHmmss}"
                + $"-{System.Diagnostics.Process.GetCurrentProcess().Id}"
                + $"-{Guid.NewGuid():N}".Substring(0, 9)
                + ".exe");

        using (var file = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            resource.CopyTo(file, 1 << 20);
        }

        return tempPath;
    }
}
