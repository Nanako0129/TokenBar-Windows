using TokenBar.Interop;

namespace TokenBar.App;

public static class GraphCompletionPolicy
{
    public static (bool IsCurrent, bool ShouldRerun, bool ClearRefreshing) Decide(
        bool isCurrent,
        bool polling,
        bool sameYear,
        bool forceRequested,
        bool refreshing)
    {
        if (!isCurrent)
        {
            return default;
        }

        var shouldRerun = polling && (!sameYear || forceRequested);
        return (true, shouldRerun, refreshing && !shouldRerun);
    }
}

public static class GraphResumePolicy
{
    public static bool ShouldRefreshAfterAttach(GraphAttachment attachment) =>
        attachment.RequestId.Query.Year is not null && !attachment.InFlight;
}

public static class GraphYearPolicy
{
    public static bool ShouldClearToAllTime(
        string? selectedYear,
        GraphPublication publication) =>
        selectedYear is not null
        && publication.RequestId.Query.Year == selectedYear
        && !publication.Payload.Years.Any(year => year.Year == selectedYear);
}

public static class GraphLazyRefreshPolicy
{
    public static bool ShouldRequest(
        GraphRequestId? scheduledRequestId,
        GraphRequestId requestId) =>
        scheduledRequestId != requestId;
}

/// <summary>Pure consumer gate for one retained graph pipeline. UI-facing
/// properties may only change after these exact request/generation checks.</summary>
public sealed class GraphConsumerState : IDisposable
{
    private readonly object _gate = new();
    private GraphRequestId? _desired;
    private GraphRequestId? _acceptedId;
    private UsagePayload? _acceptedGraph;
    private GraphPublicationStage? _acceptedStage;
    private bool _localAccepted;
    private bool _costAuthoritative;
    private bool _modelStarted;
    private bool _modelAccepted;
    private bool _disposed;

    public GraphRequestId? DesiredId
    {
        get { lock (_gate) return _desired; }
    }

    public GraphRequestId? AcceptedId
    {
        get { lock (_gate) return _acceptedId; }
    }

    public UsagePayload? AcceptedGraph
    {
        get { lock (_gate) return _acceptedGraph; }
    }

    public GraphPublicationStage? AcceptedStage
    {
        get { lock (_gate) return _acceptedStage; }
    }

    public bool LocalAccepted
    {
        get { lock (_gate) return _localAccepted; }
    }

    public bool CostAuthoritative
    {
        get { lock (_gate) return _costAuthoritative; }
    }

    public bool ModelStarted
    {
        get { lock (_gate) return _modelStarted; }
    }

    public bool ModelAccepted
    {
        get { lock (_gate) return _modelAccepted; }
    }

    public bool IsDisposed
    {
        get { lock (_gate) return _disposed; }
    }

    /// <summary>Revoke the previous generation immediately. A same-query
    /// refresh may keep a caller's old topology, but this state exposes no old
    /// graph or cost authority as current.</summary>
    public bool Begin(GraphRequestId requestId)
    {
        lock (_gate)
        {
            if (_disposed || _desired == requestId)
            {
                return false;
            }

            if (_desired is { } desired
                && desired.Query == requestId.Query
                && requestId.Generation < desired.Generation)
            {
                return false;
            }

            _desired = requestId;
            _acceptedId = null;
            _acceptedGraph = null;
            _acceptedStage = null;
            _localAccepted = false;
            _costAuthoritative = false;
            _modelStarted = false;
            _modelAccepted = false;
            return true;
        }
    }

    /// <summary>Accept only the exact desired query+generation. A richer
    /// retained publication also proves the local stage for late observers.</summary>
    public bool TryAcceptGraph(
        GraphRequestId requestId,
        UsagePayload graph,
        GraphPublicationStage stage)
    {
        ArgumentNullException.ThrowIfNull(graph);
        lock (_gate)
        {
            if (_disposed || _desired != requestId)
            {
                return false;
            }

            // Attach returns a retained snapshot after releasing the coordinator
            // lock. A same-generation richer event can therefore arrive before
            // the retained local snapshot is replayed; never let that replay
            // regress the accepted stage or revoke richer cost authority.
            if (_acceptedId == requestId
                && _acceptedStage == GraphPublicationStage.Richer
                && stage == GraphPublicationStage.LocalFirst)
            {
                return false;
            }

            _acceptedId = requestId;
            _acceptedGraph = graph;
            _acceptedStage = stage;
            _localAccepted = true;
            _costAuthoritative = CostSurfaceProjection.IsAuthoritative(graph);
            return true;
        }
    }

    /// <summary>ModelReport may start once, and only after exact-generation
    /// LocalAccepted (including a retained richer attachment).</summary>
    public bool TryBeginModel(GraphRequestId requestId)
    {
        lock (_gate)
        {
            if (_disposed || _desired != requestId || !_localAccepted || _modelStarted)
            {
                return false;
            }

            _modelStarted = true;
            return true;
        }
    }

    public bool TryAcceptModel(GraphRequestId requestId, ModelReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (_gate)
        {
            if (_disposed || _desired != requestId || !_modelStarted)
            {
                return false;
            }

            _modelAccepted = true;
            return true;
        }
    }

    public bool IsCurrent(GraphRequestId requestId)
    {
        lock (_gate)
        {
            return !_disposed && _desired == requestId;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _desired = null;
            _acceptedId = null;
            _acceptedGraph = null;
            _acceptedStage = null;
            _localAccepted = false;
            _costAuthoritative = false;
            _modelStarted = false;
            _modelAccepted = false;
        }
    }
}
