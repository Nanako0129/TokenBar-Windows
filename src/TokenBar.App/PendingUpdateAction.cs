namespace TokenBar.App;

internal sealed class PendingUpdateAction : IDisposable
{
    internal const int MaxVersionLength = 64;

    /// <summary>Settings key holding the one version the user pressed "Skip
    /// This Version" on. A bare constant rather than a reader, because this
    /// file is in TokenBar.Core.Tests' compile set and AppSettings is not —
    /// see <see cref="ShouldOffer"/>.</summary>
    internal const string SkippedVersionKey = "tokenbar.update.skippedVersion";

    private readonly object _gate = new();
    private PendingAction? _pending;
    private int _generation;
    private bool _disposed;

    internal bool Publish(string version, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ValidateVersion(version);

        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            _generation++;
            _pending = new PendingAction(this, _generation, version, action);
            return true;
        }
    }

    /// <summary>The generation of the current pending action, or null when
    /// there is none. Publish overwrites unconditionally and bumps this, so two
    /// offers of the *same version* are still distinguishable — which the
    /// version string alone cannot do, and Take() would consume.</summary>
    internal int? PeekGeneration()
    {
        lock (_gate)
        {
            return _pending is null ? null : _generation;
        }
    }

    internal string? Peek()
    {
        lock (_gate)
        {
            return _disposed ? null : _pending?.Version;
        }
    }

    internal PendingAction? Take()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            var pending = _pending;
            _pending = null;
            return pending;
        }
    }

    internal bool Restore(PendingAction pending)
    {
        ArgumentNullException.ThrowIfNull(pending);

        lock (_gate)
        {
            if (_disposed
                || _pending is not null
                || pending.Owner != this
                || pending.Generation != _generation)
            {
                return false;
            }

            pending.ResetForRestore();
            _pending = pending;
            return true;
        }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _generation++;
            _pending = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _generation++;
            _pending = null;
        }
    }

    private bool TryInvoke(PendingAction pending)
    {
        Action action;
        lock (_gate)
        {
            if (_disposed
                || pending.Owner != this
                || pending.Generation != _generation
                || pending.Invoked)
            {
                return false;
            }

            pending.Invoked = true;
            action = pending.Action;
        }

        action();
        return true;
    }

    private static void ValidateVersion(string version)
    {
        if (!TryValidateVersion(version))
        {
            throw new ArgumentException("Version is not safe to display.", nameof(version));
        }
    }

    /// <summary>The one definition of "a version string this app is willing to
    /// display". <see cref="Publish"/> throws on a false; the skip rule below
    /// resolves it to "offer the update".</summary>
    internal static bool TryValidateVersion(string? version) =>
        !string.IsNullOrEmpty(version)
        && version.Length <= MaxVersionLength
        && !version.Any(char.IsControl)
        && Version.TryParse(version, out var parsed)
        && string.Equals(parsed.ToString(), version, StringComparison.Ordinal);

    /// <summary>Whether an update should be offered at all, given whatever the
    /// skip key currently holds.
    ///
    /// <para>A pure function of two strings, deliberately: the rule has to live
    /// beside <see cref="TryValidateVersion"/> so there is one definition of a
    /// valid version, this file is in TokenBar.Core.Tests' compile set, and
    /// <c>AppSettings</c> is not. The caller (<c>App.PublishUpdate</c>) reads
    /// the key and passes the value in.</para>
    ///
    /// <para><b>Exact ordinal equality, never an ordering.</b> Under a
    /// <c>&lt;=</c> rule a stored <c>"999.0.0"</c> — which nothing stops a
    /// hand-edited settings file from containing — would permanently and
    /// silently suppress every future update. Sparkle's semantics are skip
    /// <em>this</em> version, and "a newer version is still offered" then holds
    /// for free.</para>
    ///
    /// <para>Anything that is not a well-formed version resolves to
    /// <c>true</c>: a corrupt or absent key must fail towards offering the
    /// update, not towards hiding it.</para></summary>
    internal static bool ShouldOffer(string candidateVersion, string? storedSkipped) =>
        !TryValidateVersion(storedSkipped)
        || !string.Equals(candidateVersion, storedSkipped, StringComparison.Ordinal);

    internal sealed class PendingAction
    {
        internal PendingAction(
            PendingUpdateAction owner,
            int generation,
            string version,
            Action action)
        {
            Owner = owner;
            Generation = generation;
            Version = version;
            Action = action;
        }

        internal PendingUpdateAction Owner { get; }
        internal int Generation { get; }
        internal string Version { get; }
        internal Action Action { get; }
        internal bool Invoked { get; set; }

        internal bool TryInvoke() => Owner.TryInvoke(this);

        internal void ResetForRestore() => Invoked = false;
    }
}
