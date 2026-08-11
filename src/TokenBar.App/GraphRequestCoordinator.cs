using TokenBar.Interop;

namespace TokenBar.App;

public enum GraphPublicationStage
{
    LocalFirst,
    Richer,
}

public readonly record struct GraphQuery(string? Year)
{
    public static GraphQuery Normalize(string? year) =>
        new(string.IsNullOrWhiteSpace(year) ? null : year.Trim());
}

public readonly record struct GraphRequestId(GraphQuery Query, long Generation);

public sealed record GraphPublication(
    GraphRequestId RequestId,
    GraphPublicationStage Stage,
    UsagePayload Payload);

public sealed record GraphRequestCompletion(
    GraphRequestId RequestId,
    bool LocalSucceeded,
    bool RicherSucceeded)
{
    public bool Succeeded => LocalSucceeded && RicherSucceeded;

    public bool IsSuccessfulRicherFor(GraphRequestId requestId) =>
        RequestId == requestId && RicherSucceeded;
}

public readonly record struct GraphAttachment(
    GraphRequestId RequestId,
    GraphPublication? Latest,
    bool InFlight);

/// <summary>Process-shared graph pipeline. Every production graph call enters
/// here, so local-first publication and request supersession have one owner.</summary>
public sealed class GraphRequestCoordinator
{
    private sealed class QueryState(GraphQuery query)
    {
        public GraphQuery Query { get; } = query;
        public GraphRequestId Current;
        public GraphPublication? Latest;
        public bool InFlight;
        public bool CurrentSucceeded;
    }

    private readonly object _gate = new();
    private readonly Dictionary<GraphQuery, QueryState> _states = [];
    private readonly Func<string?, UsagePayload> _localFirst;
    private readonly Func<string?, UsagePayload> _graph;
    private readonly Func<string?, UsagePayload> _refreshGraph;
    private long _nextGeneration;

    public GraphRequestCoordinator(
        Func<string?, UsagePayload>? localFirst = null,
        Func<string?, UsagePayload>? graph = null,
        Func<string?, UsagePayload>? refreshGraph = null)
    {
        _localFirst = localFirst ?? TbCore.GraphLocalFirst;
        _graph = graph ?? TbCore.Graph;
        _refreshGraph = refreshGraph ?? TbCore.RefreshGraph;
    }

    public event Action<GraphRequestId>? Started;
    public event Action<GraphPublication>? Published;
    public event Action<GraphRequestCompletion>? Completed;

    /// <summary>Attach an observer's exact query. A retained exact publication
    /// is returned synchronously; otherwise one pipeline is started.</summary>
    public GraphAttachment Attach(
        string? year,
        Action<GraphRequestId>? onBegin = null)
    {
        var query = GraphQuery.Normalize(year);
        QueryState state;
        GraphRequestId requestId;
        GraphPublication? latest;
        bool start;
        bool inFlight;
        lock (_gate)
        {
            state = GetState(query);
            if (state.InFlight)
            {
                requestId = state.Current;
                start = false;
            }
            else if (state.CurrentSucceeded && state.Latest?.RequestId == state.Current)
            {
                requestId = state.Current;
                start = false;
            }
            else
            {
                requestId = BeginLocked(state);
                start = true;
            }

        }

        if (start)
        {
            InvokeStarted(requestId);
        }

        onBegin?.Invoke(requestId);
        lock (_gate)
        {
            // The handoff callback may let a publication race before the
            // consumer begins. Replay the newest exact publication instead of
            // returning the snapshot captured before that handoff.
            latest = state.Latest?.RequestId == requestId ? state.Latest : null;
            inFlight = state.InFlight;
        }

        if (start)
        {
            StartPipeline(state, requestId, force: false);
        }

        return new GraphAttachment(requestId, latest, inFlight);
    }

    /// <summary>Start a newer generation unless the exact query is already
    /// running. Force always supersedes the previous generation.</summary>
    public GraphRequestId Request(
        string? year,
        bool force = false,
        Action<GraphRequestId>? onBegin = null)
    {
        var query = GraphQuery.Normalize(year);
        QueryState state;
        GraphRequestId requestId;
        bool start;
        lock (_gate)
        {
            state = GetState(query);
            if (!force && state.InFlight)
            {
                requestId = state.Current;
                start = false;
            }
            else
            {
                requestId = BeginLocked(state);
                start = true;
            }
        }

        if (start)
        {
            InvokeStarted(requestId);
        }

        onBegin?.Invoke(requestId);
        if (start)
        {
            StartPipeline(state, requestId, force);
        }

        return requestId;
    }

    private QueryState GetState(GraphQuery query)
    {
        if (_states.TryGetValue(query, out var state))
        {
            return state;
        }

        state = new QueryState(query);
        _states.Add(query, state);
        return state;
    }

    private GraphRequestId BeginLocked(QueryState state)
    {
        var requestId = new GraphRequestId(state.Query, ++_nextGeneration);
        state.Current = requestId;
        state.InFlight = true;
        state.CurrentSucceeded = false;
        return requestId;
    }

    private void StartPipeline(QueryState state, GraphRequestId requestId, bool force)
    {
        _ = Task.Run(() => RunPipeline(state, requestId, force));
    }

    private void RunPipeline(QueryState state, GraphRequestId requestId, bool force)
    {
        var localSucceeded = false;
        var richerSucceeded = false;
        try
        {
            UsagePayload local;
            try
            {
                local = _localFirst(requestId.Query.Year);
                localSucceeded = true;
            }
            catch
            {
                return;
            }

            if (!PublishIfCurrent(state, requestId,
                    new GraphPublication(
                        requestId, GraphPublicationStage.LocalFirst, local)))
            {
                return;
            }

            try
            {
                var richer = (force ? _refreshGraph : _graph)(requestId.Query.Year);
                richerSucceeded = true;
                PublishIfCurrent(state, requestId,
                    new GraphPublication(requestId, GraphPublicationStage.Richer, richer));
            }
            catch
            {
                // Local-first is still useful; no richer success is published.
            }
        }
        finally
        {
            var current = false;
            lock (_gate)
            {
                if (state.Current == requestId)
                {
                    state.InFlight = false;
                    state.CurrentSucceeded = localSucceeded;
                    current = true;
                }
            }

            if (current)
            {
                InvokeCompleted(new GraphRequestCompletion(
                    requestId, localSucceeded, richerSucceeded));
            }
        }
    }

    private bool PublishIfCurrent(
        QueryState state, GraphRequestId requestId, GraphPublication publication)
    {
        var current = false;
        lock (_gate)
        {
            if (state.Current == requestId)
            {
                state.Latest = publication;
                state.CurrentSucceeded = true;
                current = true;
            }
        }

        if (!current)
        {
            return false;
        }

        InvokePublished(publication);
        lock (_gate)
        {
            return state.Current == requestId;
        }
    }

    private void InvokeStarted(GraphRequestId requestId)
    {
        foreach (var observer in Started?.GetInvocationList()
            .Cast<Action<GraphRequestId>>() ?? [])
        {
            try
            {
                observer(requestId);
            }
            catch
            {
                // One consumer cannot prevent the others from revoking stale state.
            }
        }
    }

    private void InvokePublished(GraphPublication publication)
    {
        foreach (var observer in Published?.GetInvocationList()
            .Cast<Action<GraphPublication>>() ?? [])
        {
            try
            {
                observer(publication);
            }
            catch
            {
                // A consumer must not stop the shared pipeline for its peers.
            }
        }
    }

    private void InvokeCompleted(GraphRequestCompletion completion)
    {
        foreach (var observer in Completed?.GetInvocationList()
            .Cast<Action<GraphRequestCompletion>>() ?? [])
        {
            try
            {
                observer(completion);
            }
            catch
            {
                // Completion is advisory; consumer failures cannot poison peers.
            }
        }
    }
}
