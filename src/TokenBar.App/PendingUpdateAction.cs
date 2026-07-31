namespace TokenBar.App;

internal sealed class PendingUpdateAction : IDisposable
{
    internal const int MaxVersionLength = 64;

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
        ArgumentException.ThrowIfNullOrEmpty(version);
        if (version.Length > MaxVersionLength
            || version.Any(char.IsControl)
            || !Version.TryParse(version, out var parsed)
            || !string.Equals(parsed.ToString(), version, StringComparison.Ordinal))
        {
            throw new ArgumentException("Version is not safe to display.", nameof(version));
        }
    }

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
