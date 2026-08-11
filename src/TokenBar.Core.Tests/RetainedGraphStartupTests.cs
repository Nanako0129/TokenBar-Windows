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
}
