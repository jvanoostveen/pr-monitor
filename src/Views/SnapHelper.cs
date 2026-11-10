using System.Windows;

namespace PrMonitor;

/// <summary>
/// Pure calculation helpers for window corner snapping. All methods are stateless
/// and take explicit parameters so they can be unit-tested without a WPF window.
/// </summary>
internal static class SnapHelper
{
    public const double DefaultThreshold = 80;
    public const double DefaultInset = 6;

    /// <summary>
    /// Given a window rect and a work-area rect, return which corner (if any) the window
    /// is within <paramref name="threshold"/> pixels of.
    /// </summary>
    public static SnapCorner DetectNearCorner(
        double left, double top, double width, double height,
        Rect workArea, double threshold = DefaultThreshold)
    {
        bool nearLeft   = left < workArea.Left + threshold;
        bool nearRight  = left + width > workArea.Right - threshold;
        bool nearTop    = top  < workArea.Top  + threshold;
        bool nearBottom = top  + height > workArea.Bottom - threshold;

        if (nearLeft  && nearTop)    return SnapCorner.TopLeft;
        if (nearRight && nearTop)    return SnapCorner.TopRight;
        if (nearLeft  && nearBottom) return SnapCorner.BottomLeft;
        if (nearRight && nearBottom) return SnapCorner.BottomRight;
        return SnapCorner.None;
    }

    /// <summary>
    /// From a list of work-area rects, return the index of the one that best matches a window
    /// rect: first by highest overlap area, then by closest center distance.
    /// Returns -1 when the list is empty.
    /// </summary>
    public static int FindBestWorkAreaIndex(
        IReadOnlyList<Rect> workAreas,
        double left, double top, double width, double height)
    {
        var windowRect    = new Rect(left, top, width, height);
        var windowCenterX = left + width  / 2;
        var windowCenterY = top  + height / 2;

        var bestOverlap = -1;
        double maxOverlap = 0;
        var closest = -1;
        double minDist = double.MaxValue;

        for (var i = 0; i < workAreas.Count; i++)
        {
            var wa = workAreas[i];
            var overlap = ComputeOverlapArea(windowRect, wa);
            if (overlap > maxOverlap)
            {
                maxOverlap  = overlap;
                bestOverlap = i;
            }

            var cx   = (wa.Left + wa.Right)  / 2;
            var cy   = (wa.Top  + wa.Bottom) / 2;
            var dist = (windowCenterX - cx) * (windowCenterX - cx)
                     + (windowCenterY - cy) * (windowCenterY - cy);
            if (dist < minDist)
            {
                minDist = dist;
                closest = i;
            }
        }

        return bestOverlap >= 0 ? bestOverlap : closest;
    }

    /// <summary>
    /// Moves a window rect the least distance needed to fit inside <paramref name="workArea"/>;
    /// a window larger than the work area is aligned to its left/top edge.
    /// </summary>
    public static (double left, double top) ClampToWorkArea(
        Rect workArea, double left, double top, double width, double height)
    {
        var clampedLeft = width  >= workArea.Width  ? workArea.Left : Clamp(left, workArea.Left, workArea.Right  - width);
        var clampedTop  = height >= workArea.Height ? workArea.Top  : Clamp(top,  workArea.Top,  workArea.Bottom - height);
        return (clampedLeft, clampedTop);
    }

    /// <summary>
    /// Like <see cref="Math.Clamp(double, double, double)"/>, but returns <paramref name="min"/>
    /// instead of throwing when rounding leaves <paramref name="max"/> just below it.
    /// </summary>
    private static double Clamp(double value, double min, double max) =>
        max < min ? min : Math.Max(min, Math.Min(value, max));

    /// <summary>
    /// Position for a window rect that keeps it on screen: unchanged when it is fully inside one
    /// of <paramref name="workAreas"/>, otherwise clamped into the work area that needs the
    /// smallest move (largest overlap on a tie). With no work areas, clamped into
    /// <paramref name="fallbackWorkArea"/> (the primary screen) — e.g. after its monitor was
    /// disconnected.
    /// </summary>
    public static (double left, double top) ClampToBestWorkArea(
        IReadOnlyList<Rect> workAreas, Rect fallbackWorkArea,
        double left, double top, double width, double height)
    {
        var targetRect = new Rect(left, top, width, height);
        foreach (var wa in workAreas)
        {
            if (wa.Contains(targetRect.TopLeft) && wa.Contains(targetRect.BottomRight))
                return (left, top);
        }

        var bestDistance = double.PositiveInfinity;
        var bestOverlap = double.NegativeInfinity;
        var bestPosition = (left, top);

        foreach (var wa in workAreas)
        {
            var candidate = ClampToWorkArea(wa, left, top, width, height);
            var dx = candidate.left - left;
            var dy = candidate.top - top;
            var distance = (dx * dx) + (dy * dy);
            var overlap = ComputeOverlapArea(new Rect(candidate.left, candidate.top, width, height), wa);

            if (distance < bestDistance - 0.01 || (Math.Abs(distance - bestDistance) <= 0.01 && overlap > bestOverlap))
            {
                bestDistance = distance;
                bestOverlap = overlap;
                bestPosition = candidate;
            }
        }

        return double.IsPositiveInfinity(bestDistance)
            ? ClampToWorkArea(fallbackWorkArea, left, top, width, height)
            : bestPosition;
    }

    /// <summary>
    /// Calculate the top-left pixel coordinate to place a window at a specific corner
    /// of a work area, inset by <paramref name="inset"/> pixels from the edge.
    /// </summary>
    public static (double left, double top) GetCornerPosition(
        Rect workArea, SnapCorner corner, double width, double height,
        double inset = DefaultInset)
    {
        return corner switch
        {
            SnapCorner.TopLeft     => (workArea.Left + inset,              workArea.Top + inset),
            SnapCorner.TopRight    => (workArea.Right  - width  - inset,   workArea.Top + inset),
            SnapCorner.BottomLeft  => (workArea.Left + inset,              workArea.Bottom - height - inset),
            SnapCorner.BottomRight => (workArea.Right  - width  - inset,   workArea.Bottom - height - inset),
            _                      => (workArea.Left,                      workArea.Top)
        };
    }

    /// <summary>Returns the area of the intersection of two rectangles (0 when they don't overlap).</summary>
    public static double ComputeOverlapArea(Rect a, Rect b)
    {
        var x1 = Math.Max(a.Left, b.Left);
        var y1 = Math.Max(a.Top,  b.Top);
        var x2 = Math.Min(a.Right,  b.Right);
        var y2 = Math.Min(a.Bottom, b.Bottom);
        var w  = Math.Max(0, x2 - x1);
        var h  = Math.Max(0, y2 - y1);
        return w * h;
    }
}
