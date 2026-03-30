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
    /// From a list of work-area rects, return the one that best matches a window rect:
    /// first by highest overlap area, then by closest center distance.
    /// Returns <see cref="Rect.Empty"/> when the list is empty.
    /// </summary>
    public static Rect FindBestWorkArea(
        IReadOnlyList<Rect> workAreas,
        double left, double top, double width, double height)
    {
        var windowRect    = new Rect(left, top, width, height);
        var windowCenterX = left + width  / 2;
        var windowCenterY = top  + height / 2;

        Rect  bestOverlap = Rect.Empty;
        double maxOverlap = 0;
        Rect  closest     = Rect.Empty;
        double minDist    = double.MaxValue;

        foreach (var wa in workAreas)
        {
            var overlap = ComputeOverlapArea(windowRect, wa);
            if (overlap > maxOverlap)
            {
                maxOverlap  = overlap;
                bestOverlap = wa;
            }

            var cx   = (wa.Left + wa.Right)  / 2;
            var cy   = (wa.Top  + wa.Bottom) / 2;
            var dist = (windowCenterX - cx) * (windowCenterX - cx)
                     + (windowCenterY - cy) * (windowCenterY - cy);
            if (dist < minDist)
            {
                minDist = dist;
                closest = wa;
            }
        }

        if (maxOverlap > 0)     return bestOverlap;
        if (!closest.IsEmpty)   return closest;
        return workAreas.Count > 0 ? workAreas[0] : Rect.Empty;
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
