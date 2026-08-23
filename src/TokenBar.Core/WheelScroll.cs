namespace TokenBar.Core;

/// <summary>Vertical-wheel-to-horizontal-scroll policy for the flyout's tab
/// rows (macOS HorizontalWheelScroll). A mouse without a horizontal wheel only
/// emits vertical deltas, so without this the rows can only be scrolled by
/// dragging.</summary>
public static class WheelScroll
{
    /// <summary>The clamped horizontal offset after stepping, and whether it
    /// actually moved.
    ///
    /// The `moved` half is the whole point. At either edge the clamped value
    /// equals the current one, and reporting the event as consumed there would
    /// swallow the dashboard's vertical scroll for anyone whose cursor happens
    /// to rest over a row already parked at its end. macOS hit exactly that and
    /// fixed it the same way; the caller must fall through when this is
    /// false.</summary>
    public static (double Offset, bool Moved) Clamped(
        double offset, double step, double maxOffset)
    {
        var next = Math.Min(Math.Max(0, offset - step), Math.Max(0, maxOffset));
        return (next, next != offset);
    }
}
