using System.Collections.Concurrent;
using TokenBar.App;
using TokenBar.Interop;

namespace TokenBar.Core.Tests;

public class RetainedGraphStartupTests
{
    private static UsagePayload Payload() =>
        new(
            new UsageMeta(
                "generated",
                "test",
                new DateRange("2026-01-01", "2026-12-31"),
                PricingMode.BestEffort,
                CostCoverage.Complete),
            new UsageSummary(1, 2, 1, 1, 2, 2, [], []),
            [],
            []);

    [Fact]
    public async Task TrayFirstDashboardAttachReusesRichPublicationAndModelGate()
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var localCalls = 0;
        var richerCalls = 0;
        var coordinator = new GraphRequestCoordinator(
            _ =>
            {
                Interlocked.Increment(ref localCalls);
                return Payload();
            },
            _ =>
            {
                Interlocked.Increment(ref richerCalls);
                return Payload();
            });
        coordinator.Completed += _ => completion.TrySetResult(true);

        var tray = new GraphConsumerState();
        var trayAttachment = coordinator.Attach(null, id => { tray.Begin(id); });
        Assert.True(trayAttachment.InFlight);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, localCalls);
        Assert.Equal(1, richerCalls);

        var dashboard = new GraphConsumerState();
        var dashboardAttachment = coordinator.Attach(
            null, id => { dashboard.Begin(id); });
        Assert.Equal(trayAttachment.RequestId, dashboardAttachment.RequestId);
        Assert.False(dashboardAttachment.InFlight);
        Assert.NotNull(dashboardAttachment.Latest);
        Assert.True(dashboard.TryAcceptGraph(
            dashboardAttachment.RequestId,
            dashboardAttachment.Latest!.Payload,
            dashboardAttachment.Latest.Stage));
        Assert.True(dashboard.TryBeginModel(dashboardAttachment.RequestId));
        Assert.False(dashboard.TryBeginModel(dashboardAttachment.RequestId));
        Assert.Equal(1, localCalls);
        Assert.Equal(1, richerCalls);

        var newer = coordinator.Request(
            null,
            force: true,
            onBegin: id => { dashboard.Begin(id); });
        Assert.False(dashboard.TryAcceptModel(
            dashboardAttachment.RequestId,
            new ModelReport([], 0, 0, 0, 0, 0, 0)));
        Assert.True(newer.Generation > dashboardAttachment.RequestId.Generation);
    }

    [Fact]
    public async Task AttachResumeRefreshesCompletedYearsOnly()
    {
        var completions = new ConcurrentQueue<GraphRequestCompletion>();
        var localCalls = new ConcurrentDictionary<string, int>();
        var yearStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseYear = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new GraphRequestCoordinator(
            year =>
            {
                var key = year ?? "all";
                localCalls.AddOrUpdate(key, 1, (_, count) => count + 1);
                if (year == "2025")
                {
                    yearStarted.TrySetResult(true);
                    releaseYear.Task.GetAwaiter().GetResult();
                }

                return Payload();
            },
            year =>
            {
                var key = $"richer:{year ?? "all"}";
                localCalls.AddOrUpdate(key, 1, (_, count) => count + 1);
                return Payload();
            });
        coordinator.Completed += completions.Enqueue;

        var completedYear = coordinator.Request(" 2026 ");
        await WaitUntil(() => completions.Any(
            completion => completion.RequestId == completedYear));
        var completedAttachment = coordinator.Attach(" 2026 ");
        Assert.Equal("2026", completedAttachment.RequestId.Query.Year);
        Assert.False(completedAttachment.InFlight);
        Assert.NotNull(completedAttachment.Latest);
        Assert.True(GraphResumePolicy.ShouldRefreshAfterAttach(completedAttachment));

        var resumedYear = coordinator.Request(
            completedAttachment.RequestId.Query.Year,
            force: false);
        Assert.True(resumedYear.Generation > completedYear.Generation);
        await WaitUntil(() => completions.Any(
            completion => completion.RequestId == resumedYear));
        Assert.Equal(2, localCalls["2026"]);

        var inFlightYear = coordinator.Request("2025");
        await yearStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var inFlightAttachment = coordinator.Attach(" 2025 ");
        Assert.Equal(inFlightYear, inFlightAttachment.RequestId);
        Assert.True(inFlightAttachment.InFlight);
        Assert.False(GraphResumePolicy.ShouldRefreshAfterAttach(inFlightAttachment));
        if (GraphResumePolicy.ShouldRefreshAfterAttach(inFlightAttachment))
        {
            coordinator.Request(inFlightAttachment.RequestId.Query.Year);
        }

        releaseYear.TrySetResult(true);
        await WaitUntil(() => completions.Any(
            completion => completion.RequestId == inFlightYear));
        Assert.Equal(1, localCalls["2025"]);

        var allTime = coordinator.Request(null);
        await WaitUntil(() => completions.Any(
            completion => completion.RequestId == allTime));
        var allTimeCalls = localCalls["all"];
        var allTimeAttachment = coordinator.Attach(null);
        Assert.Equal(allTime, allTimeAttachment.RequestId);
        Assert.False(allTimeAttachment.InFlight);
        Assert.False(GraphResumePolicy.ShouldRefreshAfterAttach(allTimeAttachment));
        Assert.Equal(allTimeCalls, localCalls["all"]);
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
