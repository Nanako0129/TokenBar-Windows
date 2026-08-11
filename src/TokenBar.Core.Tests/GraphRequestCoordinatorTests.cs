using System.Collections.Concurrent;
using TokenBar.App;
using TokenBar.Interop;

namespace TokenBar.Core.Tests;

public class GraphRequestCoordinatorTests
{
    private static UsagePayload Payload(
        string? year,
        PricingMode pricing = PricingMode.LocalOnly,
        CostCoverage coverage = CostCoverage.Complete) =>
        new(
            new UsageMeta(
                "generated",
                "test",
                new DateRange("2026-01-01", "2026-12-31"),
                pricing,
                coverage),
            new UsageSummary(1, 2, 1, 1, 2, 2, [], []),
            year is null ? [] : [new YearMeta(year, 1, 2,
                new DateRange($"{year}-01-01", $"{year}-12-31"))],
            []);

    [Fact]
    public async Task LocalPublishesBeforeBlockedRicherAndRetainedAttachAddsNoCalls()
    {
        var richerGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var richerStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var localPublished = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<GraphRequestCompletion>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var publications = new ConcurrentQueue<GraphPublication>();
        var localCalls = 0;
        var richerCalls = 0;
        var local = Payload(null);
        var richer = Payload(null, PricingMode.BestEffort, CostCoverage.Complete);
        var coordinator = new GraphRequestCoordinator(
            _ =>
            {
                Interlocked.Increment(ref localCalls);
                return local;
            },
            _ =>
            {
                Interlocked.Increment(ref richerCalls);
                richerStarted.TrySetResult(true);
                richerGate.Task.GetAwaiter().GetResult();
                return richer;
            });
        coordinator.Published += publication =>
        {
            publications.Enqueue(publication);
            if (publication.Stage == GraphPublicationStage.LocalFirst)
            {
                localPublished.TrySetResult(true);
            }
        };
        coordinator.Completed += completion => completed.TrySetResult(completion);

        var requestId = coordinator.Request(null);
        await localPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(GraphPublicationStage.LocalFirst,
            publications.First().Stage);
        await richerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, localCalls);
        Assert.Equal(1, richerCalls);

        var retained = coordinator.Attach(null);
        Assert.Equal(requestId, retained.RequestId);
        Assert.True(retained.InFlight);
        Assert.Equal(GraphPublicationStage.LocalFirst, retained.Latest?.Stage);

        richerGate.TrySetResult(true);
        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Succeeded);
        Assert.Equal(2, publications.Count);

        var late = coordinator.Attach(null);
        Assert.Equal(requestId, late.RequestId);
        Assert.False(late.InFlight);
        Assert.Equal(GraphPublicationStage.Richer, late.Latest?.Stage);
        var consumer = new GraphConsumerState();
        consumer.Begin(late.RequestId);
        Assert.True(consumer.TryAcceptGraph(
            late.RequestId,
            late.Latest!.Payload,
            late.Latest.Stage));
        Assert.True(consumer.LocalAccepted);
        Assert.Equal(1, localCalls);
        Assert.Equal(1, richerCalls);
    }

    [Fact]
    public async Task AttachReplaysRicherPublishedBeforeBegin()
    {
        var localPublished = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var richerStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var richerPublishedRejected = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var richerGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new GraphRequestCoordinator(
            _ =>
            {
                localPublished.TrySetResult(true);
                return Payload(null, PricingMode.LocalOnly, CostCoverage.Complete);
            },
            _ =>
            {
                richerStarted.TrySetResult(true);
                richerGate.Task.GetAwaiter().GetResult();
                return Payload(null, PricingMode.BestEffort, CostCoverage.Complete);
            });
        coordinator.Published += publication =>
        {
            if (publication.Stage == GraphPublicationStage.LocalFirst)
            {
                localPublished.TrySetResult(true);
            }
        };

        var requestId = coordinator.Request(null);
        await localPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await richerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var retained = coordinator.Attach(null);
        Assert.Equal(requestId, retained.RequestId);
        Assert.Equal(GraphPublicationStage.LocalFirst, retained.Latest?.Stage);

        var consumer = new GraphConsumerState();
        coordinator.Published += publication =>
        {
            if (publication.RequestId == requestId
                && publication.Stage == GraphPublicationStage.Richer
                && !consumer.TryAcceptGraph(
                    publication.RequestId, publication.Payload, publication.Stage))
            {
                richerPublishedRejected.TrySetResult(true);
            }
        };

        var beginEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowBegin = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var attachmentTask = Task.Run(() => coordinator.Attach(
            null,
            id =>
            {
                beginEntered.TrySetResult(true);
                allowBegin.Task.GetAwaiter().GetResult();
                Assert.True(consumer.Begin(id));
            }));

        await beginEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(consumer.DesiredId);
        richerGate.TrySetResult(true);
        await richerPublishedRejected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(consumer.DesiredId);

        allowBegin.TrySetResult(true);
        var attachment = await attachmentTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(requestId, attachment.RequestId);
        Assert.Equal(GraphPublicationStage.Richer, attachment.Latest?.Stage);
        Assert.True(consumer.TryAcceptGraph(
            attachment.RequestId,
            attachment.Latest!.Payload,
            attachment.Latest.Stage));
        Assert.True(consumer.CostAuthoritative);
    }

    [Fact]
    public async Task AttachAndNonForceRequestCoalesceInFlight()
    {
        var localGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new GraphRequestCoordinator(
            _ =>
            {
                Interlocked.Increment(ref calls);
                started.TrySetResult(true);
                localGate.Task.GetAwaiter().GetResult();
                return Payload("2026");
            },
            _ => Payload("2026", PricingMode.BestEffort, CostCoverage.Complete));
        coordinator.Completed += _ => completion.TrySetResult(true);

        var first = coordinator.Attach(" 2026 ");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = coordinator.Attach("2026");
        var third = coordinator.Request("2026");

        Assert.Equal(first.RequestId, second.RequestId);
        Assert.Equal(second.RequestId, third);
        Assert.Equal(1, calls);

        localGate.TrySetResult(true);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ForceSupersedesOldRicherAndPublishesOnlyCurrentRicher()
    {
        var oldRicherGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRicherStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completions = new ConcurrentQueue<GraphRequestCompletion>();
        var publications = new ConcurrentQueue<GraphPublication>();
        var localCalls = 0;
        var coordinator = new GraphRequestCoordinator(
            _ =>
            {
                Interlocked.Increment(ref localCalls);
                return Payload(null);
            },
            _ =>
            {
                oldRicherStarted.TrySetResult(true);
                oldRicherGate.Task.GetAwaiter().GetResult();
                return Payload(null, PricingMode.BestEffort, CostCoverage.Complete);
            },
            _ => Payload(null, PricingMode.BestEffort, CostCoverage.Complete));
        coordinator.Published += publications.Enqueue;
        coordinator.Completed += completions.Enqueue;

        var oldId = coordinator.Request(null);
        await oldRicherStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var currentId = coordinator.Request(null, force: true);
        Assert.True(currentId.Generation > oldId.Generation);

        await WaitUntil(() => completions.Any(c => c.RequestId == currentId));
        oldRicherGate.TrySetResult(true);
        await Task.Delay(50);

        Assert.Equal(2, localCalls);
        Assert.Contains(publications, p => p.RequestId == currentId
            && p.Stage == GraphPublicationStage.Richer);
        Assert.DoesNotContain(publications, p => p.RequestId == oldId
            && p.Stage == GraphPublicationStage.Richer);
    }

    [Fact]
    public async Task StartedBroadcastKeepsAllTimeConsumersOnOneGeneration()
    {
        var secondLocalGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCompletion = new TaskCompletionSource<GraphRequestCompletion>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCompletion = new TaskCompletionSource<GraphRequestCompletion>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var localCalls = 0;
        var coordinator = new GraphRequestCoordinator(
            _ =>
            {
                if (Interlocked.Increment(ref localCalls) == 2)
                {
                    secondLocalGate.Task.GetAwaiter().GetResult();
                }

                return Payload(null, PricingMode.BestEffort, CostCoverage.Complete);
            },
            _ => Payload(null, PricingMode.BestEffort, CostCoverage.Complete),
            _ => Payload(null, PricingMode.BestEffort, CostCoverage.Complete));
        var tray = new GraphConsumerState();
        var dashboard = new GraphConsumerState();
        var starts = new ConcurrentQueue<GraphRequestId>();
        coordinator.Started += _ => throw new InvalidOperationException("observer");
        coordinator.Started += id =>
        {
            starts.Enqueue(id);
            if (id.Query.Year is null)
            {
                tray.Begin(id);
            }
        };
        coordinator.Started += id =>
        {
            if (id.Query.Year is null)
            {
                dashboard.Begin(id);
            }
        };
        coordinator.Published += publication =>
        {
            tray.TryAcceptGraph(
                publication.RequestId, publication.Payload, publication.Stage);
            dashboard.TryAcceptGraph(
                publication.RequestId, publication.Payload, publication.Stage);
        };
        coordinator.Completed += completion =>
        {
            if (completion.RequestId.Generation == 1)
            {
                firstCompletion.TrySetResult(completion);
            }
            else
            {
                secondCompletion.TrySetResult(completion);
            }
        };

        var first = coordinator.Request(null);
        await firstCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(tray.CostAuthoritative);
        Assert.True(dashboard.CostAuthoritative);

        var second = coordinator.Request(null, force: true,
            onBegin: id => dashboard.Begin(id));
        Assert.Equal(second, tray.DesiredId);
        Assert.Equal(second, dashboard.DesiredId);
        Assert.False(tray.CostAuthoritative);
        Assert.False(dashboard.CostAuthoritative);

        secondLocalGate.TrySetResult(true);
        await secondCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(tray.CostAuthoritative);
        Assert.True(dashboard.CostAuthoritative);

        var retained = coordinator.Attach(null);
        Assert.Equal(second, retained.RequestId);
        Assert.Equal(2, starts.Count);
    }

    [Fact]
    public async Task YearStartedDoesNotMoveAllTimeTrayState()
    {
        var firstCompletion = new TaskCompletionSource<GraphRequestCompletion>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var yearCompletion = new TaskCompletionSource<GraphRequestCompletion>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new GraphRequestCoordinator(
            year => Payload(year, PricingMode.BestEffort, CostCoverage.Complete),
            year => Payload(year, PricingMode.BestEffort, CostCoverage.Complete));
        var tray = new GraphConsumerState();
        var dashboard = new GraphConsumerState();
        coordinator.Started += id =>
        {
            if (id.Query.Year is null)
            {
                tray.Begin(id);
            }
        };
        coordinator.Started += id =>
        {
            if (id.Query.Year == "2026")
            {
                dashboard.Begin(id);
            }
        };
        coordinator.Published += publication =>
        {
            tray.TryAcceptGraph(
                publication.RequestId, publication.Payload, publication.Stage);
            dashboard.TryAcceptGraph(
                publication.RequestId, publication.Payload, publication.Stage);
        };
        coordinator.Completed += completion =>
        {
            if (completion.RequestId.Query.Year is null)
            {
                firstCompletion.TrySetResult(completion);
            }
            else
            {
                yearCompletion.TrySetResult(completion);
            }
        };

        var allTime = coordinator.Request(null);
        await firstCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(tray.CostAuthoritative);

        var year = coordinator.Request("2026");
        Assert.Equal(allTime, tray.DesiredId);
        Assert.True(tray.CostAuthoritative);
        Assert.Equal(year, dashboard.DesiredId);

        await yearCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(allTime, tray.DesiredId);
        Assert.True(tray.CostAuthoritative);
        Assert.True(dashboard.CostAuthoritative);
    }

    [Fact]
    public async Task SupersededBlockedLocalSkipsOldRicherDelegate()
    {
        var oldLocalStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var oldLocalGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var newerCompletion = new TaskCompletionSource<GraphRequestCompletion>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var localCalls = 0;
        var normalRicherCalls = 0;
        var refreshRicherCalls = 0;
        var coordinator = new GraphRequestCoordinator(
            _ =>
            {
                if (Interlocked.Increment(ref localCalls) == 1)
                {
                    oldLocalStarted.TrySetResult(true);
                    oldLocalGate.Task.GetAwaiter().GetResult();
                }

                return Payload(null);
            },
            _ =>
            {
                Interlocked.Increment(ref normalRicherCalls);
                return Payload(null, PricingMode.BestEffort, CostCoverage.Complete);
            },
            _ =>
            {
                Interlocked.Increment(ref refreshRicherCalls);
                return Payload(null, PricingMode.BestEffort, CostCoverage.Complete);
            });
        coordinator.Completed += completion =>
        {
            if (completion.RequestId.Generation > 1)
            {
                newerCompletion.TrySetResult(completion);
            }
        };

        var old = coordinator.Request(null);
        await oldLocalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var newer = coordinator.Request(null, force: true);
        var result = await newerCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(newer, result.RequestId);
        Assert.True(result.RicherSucceeded);
        Assert.Equal(1, refreshRicherCalls);

        oldLocalGate.TrySetResult(true);
        await Task.Delay(100);
        Assert.Equal(0, normalRicherCalls);
        Assert.True(newer.Generation > old.Generation);
    }

    [Fact]
    public void CompletionMatchesOnlyExactSuccessfulRicherRequest()
    {
        var allTime = new GraphRequestId(GraphQuery.Normalize(null), 7);
        var otherGeneration = new GraphRequestId(GraphQuery.Normalize(null), 6);
        var year = new GraphRequestId(GraphQuery.Normalize("2026"), 7);

        Assert.True(new GraphRequestCompletion(allTime, true, true)
            .IsSuccessfulRicherFor(allTime));
        Assert.False(new GraphRequestCompletion(allTime, true, false)
            .IsSuccessfulRicherFor(allTime));
        Assert.False(new GraphRequestCompletion(allTime, true, true)
            .IsSuccessfulRicherFor(otherGeneration));
        Assert.False(new GraphRequestCompletion(year, true, true)
            .IsSuccessfulRicherFor(allTime));
    }

    [Fact]
    public async Task StageFailuresPublishNoSuccessForThatStage()
    {
        var localCompleted = new TaskCompletionSource<GraphRequestCompletion>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var localPublications = new ConcurrentQueue<GraphPublication>();
        var richerCalls = 0;
        var localFailing = new GraphRequestCoordinator(
            _ => throw new InvalidOperationException("blocked local"),
            _ =>
            {
                Interlocked.Increment(ref richerCalls);
                return Payload(null, PricingMode.BestEffort, CostCoverage.Complete);
            });
        localFailing.Published += localPublications.Enqueue;
        localFailing.Completed += completion =>
            localCompleted.TrySetResult(completion);
        var localId = localFailing.Request(null);
        var localResult = await localCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(localId, localResult.RequestId);
        Assert.False(localResult.Succeeded);
        Assert.Empty(localPublications);
        Assert.Equal(0, richerCalls);

        var richerCompleted = new TaskCompletionSource<GraphRequestCompletion>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var richerPublications = new ConcurrentQueue<GraphPublication>();
        var richerFailing = new GraphRequestCoordinator(
            _ => Payload(null),
            _ => throw new InvalidOperationException("blocked richer"));
        richerFailing.Published += richerPublications.Enqueue;
        richerFailing.Completed += completion =>
            richerCompleted.TrySetResult(completion);
        var richerId = richerFailing.Request(null);
        var richerResult = await richerCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(richerId, richerResult.RequestId);
        Assert.False(richerResult.Succeeded);
        Assert.Single(richerPublications);
        Assert.Equal(GraphPublicationStage.LocalFirst,
            richerPublications.Single().Stage);
    }

    private static async Task WaitForCompletion(
        GraphRequestCoordinator coordinator,
        GraphRequestId requestId)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(GraphRequestCompletion value)
        {
            if (value.RequestId == requestId)
            {
                completion.TrySetResult(true);
            }
        }

        coordinator.Completed += OnCompleted;
        try
        {
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            coordinator.Completed -= OnCompleted;
        }
    }

    private static async Task WaitUntil(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("condition was not reached");
            }

            await Task.Delay(10);
        }
    }
}
