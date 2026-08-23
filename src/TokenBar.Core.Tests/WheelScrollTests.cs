using Xunit;

namespace TokenBar.Core.Tests;

public class WheelScrollTests
{
    // A wheel notch is positive scrolling up; the row should move left, so the
    // offset decreases by the delta.
    [Theory]
    [InlineData(100, 20, 500, 80, true)]
    [InlineData(100, -20, 500, 120, true)]
    public void StepsWithinRangeMove(
        double offset, double step, double max, double expected, bool moved)
    {
        var r = WheelScroll.Clamped(offset, step, max);
        Assert.Equal(expected, r.Offset);
        Assert.Equal(moved, r.Moved);
    }

    // The half that matters: at an edge the clamp is a no-op, and reporting it
    // as consumed would swallow the dashboard's vertical scroll.
    [Theory]
    [InlineData(0, 20, 500)]      // already left, pushed further left
    [InlineData(500, -20, 500)]   // already right, pushed further right
    public void EdgesReportNotMoved(double offset, double step, double max)
    {
        var r = WheelScroll.Clamped(offset, step, max);
        Assert.Equal(offset, r.Offset);
        Assert.False(r.Moved);
    }

    [Fact]
    public void OvershootClampsAndStillCountsAsMoved()
    {
        var a = WheelScroll.Clamped(10, 200, 500);
        Assert.Equal(0, a.Offset);
        Assert.True(a.Moved);

        var b = WheelScroll.Clamped(490, -200, 500);
        Assert.Equal(500, b.Offset);
        Assert.True(b.Moved);
    }

    // A row narrower than its viewport reports a negative ScrollableWidth in
    // WinUI; nothing should scroll and nothing should be consumed.
    [Fact]
    public void UnscrollableRowNeverMoves()
    {
        Assert.False(WheelScroll.Clamped(0, 20, -1).Moved);
        Assert.False(WheelScroll.Clamped(0, -20, 0).Moved);
    }
}
