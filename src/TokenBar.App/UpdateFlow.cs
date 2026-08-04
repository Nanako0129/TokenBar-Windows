using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace TokenBar.App;

internal class UpdateFlow
{
    internal const string RepositoryUrl =
        "https://github.com/Nanako0129/TokenBar-Windows";
    /// <summary>Durable Velopack package identity. Independent of
    /// $(TbProductName): a future product rename must not move it by
    /// accident. PackageIdMatchesPackagingScript pins the two together.</summary>
    internal const string PackageId = "Nyanako.Syrtis";
    private const int MaxNuspecBytes = 65_536;
    private const long MaxCompressionRatio = 100;

    /// <summary>
    /// Exact Velopack channels accepted for installed products. Full and Lite
    /// share architecture mapping; any other string fails closed.
    /// </summary>
    internal static readonly HashSet<string> AcceptedChannels = new(StringComparer.Ordinal)
    {
        "win-x64",
        "win-x64-lite",
        "win-arm64",
        "win-arm64-lite",
    };

    private readonly ManagedUpdateManager _manager;
    private int _downloadActive;

    internal UpdateFlow(
        IFileDownloader? downloader = null,
        IVelopackLocator? locator = null)
    {
        var source = new GithubSource(
            RepositoryUrl,
            accessToken: null,
            prerelease: false,
            downloader);
        var options = new UpdateOptions
        {
            ExplicitChannel = null,
            AllowVersionDowngrade = false,
        };
        _manager = new ManagedUpdateManager(source, options, locator);
    }

    internal async Task<UpdateCandidate?> CheckForUpdatesAsync()
    {
        _ = GetInstallation();
        var update = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        return update is null ? null : ValidateTarget(update);
    }

    internal async Task DownloadAndVerifyAsync(
        UpdateCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (Interlocked.CompareExchange(ref _downloadActive, 1, 0) != 0)
        {
            throw new InvalidOperationException("An update download is already active.");
        }

        try
        {
            var validated = ValidateCandidate(candidate);
            await _manager.DownloadUpdatesAsync(
                validated.Update,
                progress: null,
                cancellationToken).ConfigureAwait(false);

            validated = ValidateCandidate(candidate);
            await _manager.VerifyChecksumAsync(
                validated.Target,
                validated.PackagePath).ConfigureAwait(false);
            ValidateNuspec(validated);
        }
        catch
        {
            _manager.CleanAllPackages();
            throw;
        }
        finally
        {
            Volatile.Write(ref _downloadActive, 0);
        }
    }

    internal bool TryHandoff(
        UpdateCandidate candidate,
        Func<bool> canHandoff,
        Action quit)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(canHandoff);
        ArgumentNullException.ThrowIfNull(quit);

        var validated = ValidateCandidate(candidate);
        if (!canHandoff())
        {
            return false;
        }

        WaitExitThenApplyUpdates(
            validated.Target,
            silent: false,
            restart: true,
            Array.Empty<string>());
        quit();
        return true;
    }

    protected virtual void WaitExitThenApplyUpdates(
        VelopackAsset target,
        bool silent,
        bool restart,
        string[] restartArgs)
    {
        _manager.WaitExitThenApplyUpdates(target, silent, restart, restartArgs);
    }

    private UpdateCandidate ValidateCandidate(UpdateCandidate candidate)
    {
        var validated = ValidateTarget(candidate.Update);
        if (!ReferenceEquals(validated.Target, candidate.Target)
            || !string.Equals(validated.Version, candidate.Version, StringComparison.Ordinal)
            || !string.Equals(validated.Channel, candidate.Channel, StringComparison.Ordinal)
            || !string.Equals(validated.Architecture, candidate.Architecture, StringComparison.Ordinal)
            || !string.Equals(validated.PackagePath, candidate.PackagePath, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Update target changed after validation.");
        }

        return validated;
    }

    private UpdateCandidate ValidateTarget(UpdateInfo update)
    {
        var installation = GetInstallation();
        var target = update.TargetFullRelease
            ?? throw new InvalidDataException("Update target is missing.");
        var version = target.Version?.ToNormalizedString()
            ?? throw new InvalidDataException("Update version is missing.");

        if (target.Type != VelopackAssetType.Full
            || !string.Equals(target.PackageId, PackageId, StringComparison.Ordinal)
            || target.Version <= installation.Version
            || target.Version.IsPrerelease
            || version.Length is 0 or > PendingUpdateAction.MaxVersionLength
            || version.Any(char.IsControl)
            || !IsSha256(target.SHA256)
            || target.Size <= 0)
        {
            throw new InvalidDataException("Update target metadata is invalid.");
        }

        var fileName = $"{PackageId}-{version}-{installation.Channel}-full.nupkg";
        if (!string.Equals(target.FileName, fileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Update filename is invalid.");
        }

        return new UpdateCandidate(
            update,
            target,
            version,
            installation.Channel,
            installation.Architecture,
            Path.Combine(installation.PackagesDirectory, fileName));
    }

    private Installation GetInstallation()
    {
        if (!_manager.IsInstalled
            || !string.Equals(_manager.AppId, PackageId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Installed package identity is invalid.");
        }

        var version = _manager.CurrentVersion
            ?? throw new InvalidOperationException("Installed version is missing.");
        var channel = _manager.InstalledChannel
            ?? throw new InvalidOperationException("Installed channel is missing.");
        var architecture = MapChannelToArchitecture(channel);
        var packagesDirectory = _manager.PackagesDirectory;
        if (string.IsNullOrWhiteSpace(packagesDirectory))
        {
            throw new InvalidOperationException("Installed packages path is missing.");
        }

        return new Installation(version, channel, architecture, packagesDirectory);
    }

    /// <summary>
    /// Maps an installed Velopack channel to PE architecture. Fail-closed for
    /// near-miss and unknown channels (including null/empty).
    /// </summary>
    internal static string MapChannelToArchitecture(string? channel)
    {
        if (channel is null
            || !AcceptedChannels.Contains(channel))
        {
            throw new InvalidOperationException("Installed channel is invalid.");
        }

        return channel.StartsWith("win-arm64", StringComparison.Ordinal)
            ? "arm64"
            : "x64";
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9'
            or >= 'a' and <= 'f'
            or >= 'A' and <= 'F');

    private static void ValidateNuspec(UpdateCandidate candidate)
    {
        using var archive = ZipFile.OpenRead(candidate.PackagePath);
        var entries = archive.Entries
            .Where(entry => !entry.FullName.EndsWith("/", StringComparison.Ordinal)
                && entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (entries.Length != 1)
        {
            throw new InvalidDataException("Package must contain exactly one nuspec.");
        }

        var entry = entries[0];
        if (entry.Length is < 1 or > MaxNuspecBytes
            || entry.CompressedLength is < 1 or > MaxNuspecBytes
            || entry.Length > entry.CompressedLength * MaxCompressionRatio)
        {
            throw new InvalidDataException("Nuspec size is invalid.");
        }

        var bytes = new byte[MaxNuspecBytes + 1];
        var length = 0;
        using (var stream = entry.Open())
        {
            while (length < bytes.Length)
            {
                var read = stream.Read(bytes, length, bytes.Length - length);
                if (read == 0)
                {
                    break;
                }

                length += read;
            }
        }

        if (length is < 1 or > MaxNuspecBytes || length != entry.Length)
        {
            throw new InvalidDataException("Nuspec content length is invalid.");
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxNuspecBytes,
            MaxCharactersFromEntities = 0,
        };
        using var input = new MemoryStream(bytes, 0, length, writable: false);
        using var reader = XmlReader.Create(input, settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        var root = document.Root;
        if (root?.Name.LocalName != "package")
        {
            throw new InvalidDataException("Nuspec package root is invalid.");
        }

        var metadata = SingleElement(root, "metadata");
        RequireValue(metadata, "id", PackageId);
        RequireValue(metadata, "version", candidate.Version);
        RequireValue(metadata, "channel", candidate.Channel);
        RequireValue(metadata, "machineArchitecture", candidate.Architecture);
    }

    private static XElement SingleElement(XElement parent, string name)
    {
        var elements = parent.Elements()
            .Where(element => element.Name.LocalName == name)
            .ToArray();
        return elements.Length == 1
            ? elements[0]
            : throw new InvalidDataException($"Nuspec {name} is invalid.");
    }

    private static void RequireValue(XElement metadata, string name, string expected)
    {
        var element = SingleElement(metadata, name);
        if (!string.Equals(element.Value, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Nuspec {name} is invalid.");
        }
    }

    private sealed class ManagedUpdateManager(
        IUpdateSource source,
        UpdateOptions options,
        IVelopackLocator? locator)
        : UpdateManager(source, options, locator)
    {
        internal string? InstalledChannel => Locator.Channel;
        internal string? PackagesDirectory => Locator.PackagesDir;

        internal Task VerifyChecksumAsync(VelopackAsset target, string packagePath) =>
            VerifyPackageChecksumAsync(target, packagePath);

        internal void CleanAllPackages() => CleanPackagesExcept(null);
    }

    private sealed record Installation(
        SemanticVersion Version,
        string Channel,
        string Architecture,
        string PackagesDirectory);
}

internal sealed record UpdateCandidate(
    UpdateInfo Update,
    VelopackAsset Target,
    string Version,
    string Channel,
    string Architecture,
    string PackagePath);
