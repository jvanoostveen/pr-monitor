using System.Windows;
using Xunit;

namespace PrMonitor.Tests.Views;

public class SnapHelperTests
{
    // Two-screen setup used across tests
    private static readonly Rect Primary   = new Rect(0,    0, 2560, 1440);
    private static readonly Rect Secondary = new Rect(2560, 0, 1920, 1080);

    private const double W = 380;
    private const double H = 590;

    // ── DetectNearCorner ────────────────────────────────────────────────────

    [Fact]
    public void DetectNearCorner_TopLeft_ReturnsTopLeft()
    {
        var result = SnapHelper.DetectNearCorner(10, 10, W, H, Primary);
        Assert.Equal(SnapCorner.TopLeft, result);
    }

    [Fact]
    public void DetectNearCorner_TopRight_ReturnsTopRight()
    {
        var left = Primary.Right - W - 10;
        var result = SnapHelper.DetectNearCorner(left, 10, W, H, Primary);
        Assert.Equal(SnapCorner.TopRight, result);
    }

    [Fact]
    public void DetectNearCorner_BottomLeft_ReturnsBottomLeft()
    {
        var top = Primary.Bottom - H - 10;
        var result = SnapHelper.DetectNearCorner(10, top, W, H, Primary);
        Assert.Equal(SnapCorner.BottomLeft, result);
    }

    [Fact]
    public void DetectNearCorner_BottomRight_ReturnsBottomRight()
    {
        var left = Primary.Right - W - 10;
        var top  = Primary.Bottom - H - 10;
        var result = SnapHelper.DetectNearCorner(left, top, W, H, Primary);
        Assert.Equal(SnapCorner.BottomRight, result);
    }

    [Fact]
    public void DetectNearCorner_Center_ReturnsNone()
    {
        // Place window well away from all edges
        var left = (Primary.Width - W) / 2;
        var top  = (Primary.Height - H) / 2;
        var result = SnapHelper.DetectNearCorner(left, top, W, H, Primary);
        Assert.Equal(SnapCorner.None, result);
    }

    [Fact]
    public void DetectNearCorner_SecondaryScreen_UsesSecondaryBounds()
    {
        // Window near top-left of secondary monitor
        var result = SnapHelper.DetectNearCorner(2570, 10, W, H, Secondary);
        Assert.Equal(SnapCorner.TopLeft, result);
    }

    [Fact]
    public void DetectNearCorner_SecondaryScreen_BottomRight()
    {
        var left = Secondary.Right - W - 10;
        var top  = Secondary.Bottom - H - 10;
        var result = SnapHelper.DetectNearCorner(left, top, W, H, Secondary);
        Assert.Equal(SnapCorner.BottomRight, result);
    }

    // Boundary: exactly at threshold distance — still "near"
    [Fact]
    public void DetectNearCorner_ExactlyAtThreshold_StillNear()
    {
        // left == workArea.Left + threshold - 1  → nearLeft == true
        var left = Primary.Left + SnapHelper.DefaultThreshold - 1;
        var top  = Primary.Top + SnapHelper.DefaultThreshold - 1;
        var result = SnapHelper.DetectNearCorner(left, top, W, H, Primary);
        Assert.Equal(SnapCorner.TopLeft, result);
    }

    // Boundary: one pixel beyond threshold — no longer near that edge
    [Fact]
    public void DetectNearCorner_OnePastThreshold_ReturnsNone()
    {
        // Place so no edge is within threshold
        var left = Primary.Left + SnapHelper.DefaultThreshold + 1;
        var top  = Primary.Top  + SnapHelper.DefaultThreshold + 1;
        // right edge: left + W must be <= Right - threshold - 1
        // With Primary 2560 wide and left=81, right=461, Right-threshold=2480 → fine
        var result = SnapHelper.DetectNearCorner(left, top, W, H, Primary);
        Assert.Equal(SnapCorner.None, result);
    }

    // ── FindBestWorkArea ────────────────────────────────────────────────────

    private static readonly IReadOnlyList<Rect> TwoScreens = new[] { Primary, Secondary };

    [Fact]
    public void FindBestWorkArea_WindowFullyOnPrimary_ReturnsPrimary()
    {
        var result = SnapHelper.FindBestWorkArea(TwoScreens, 100, 100, W, H);
        Assert.Equal(Primary, result);
    }

    [Fact]
    public void FindBestWorkArea_WindowFullyOnSecondary_ReturnsSecondary()
    {
        var result = SnapHelper.FindBestWorkArea(TwoScreens, 2700, 100, W, H);
        Assert.Equal(Secondary, result);
    }

    [Fact]
    public void FindBestWorkArea_StradlingMajorityOnSecondary_ReturnsSecondary()
    {
        // Window starts at 2510: 50 px on primary, 330 px on secondary
        var result = SnapHelper.FindBestWorkArea(TwoScreens, 2510, 100, W, H);
        Assert.Equal(Secondary, result);
    }

    [Fact]
    public void FindBestWorkArea_StradlingMajorityOnPrimary_ReturnsPrimary()
    {
        // Window starts at 2230: 330 px on primary, 50 px on secondary
        var result = SnapHelper.FindBestWorkArea(TwoScreens, 2230, 100, W, H);
        Assert.Equal(Primary, result);
    }

    [Fact]
    public void FindBestWorkArea_NoOverlapClosestToSecondary_ReturnsSecondary()
    {
        // Window is fully to the right of secondary
        var left = Secondary.Right + 100;
        var result = SnapHelper.FindBestWorkArea(TwoScreens, left, 100, W, H);
        Assert.Equal(Secondary, result);
    }

    [Fact]
    public void FindBestWorkArea_EmptyList_ReturnsRectEmpty()
    {
        var result = SnapHelper.FindBestWorkArea(Array.Empty<Rect>(), 100, 100, W, H);
        Assert.Equal(Rect.Empty, result);
    }

    // ── GetCornerPosition ───────────────────────────────────────────────────

    [Fact]
    public void GetCornerPosition_TopLeft_Correct()
    {
        var (l, t) = SnapHelper.GetCornerPosition(Primary, SnapCorner.TopLeft, W, H);
        Assert.Equal(6, l);
        Assert.Equal(6, t);
    }

    [Fact]
    public void GetCornerPosition_TopRight_Correct()
    {
        var (l, t) = SnapHelper.GetCornerPosition(Primary, SnapCorner.TopRight, W, H);
        Assert.Equal(2560 - W - 6, l);
        Assert.Equal(6, t);
    }

    [Fact]
    public void GetCornerPosition_BottomLeft_Correct()
    {
        var (l, t) = SnapHelper.GetCornerPosition(Primary, SnapCorner.BottomLeft, W, H);
        Assert.Equal(6, l);
        Assert.Equal(1440 - H - 6, t);
    }

    [Fact]
    public void GetCornerPosition_BottomRight_Correct()
    {
        var (l, t) = SnapHelper.GetCornerPosition(Primary, SnapCorner.BottomRight, W, H);
        Assert.Equal(2560 - W - 6, l);
        Assert.Equal(1440 - H - 6, t);
    }

    [Fact]
    public void GetCornerPosition_None_ReturnsWorkAreaOrigin()
    {
        var (l, t) = SnapHelper.GetCornerPosition(Primary, SnapCorner.None, W, H);
        Assert.Equal(Primary.Left, l);
        Assert.Equal(Primary.Top,  t);
    }

    [Fact]
    public void GetCornerPosition_CustomInset_UsesCustomInset()
    {
        var (l, t) = SnapHelper.GetCornerPosition(Primary, SnapCorner.TopLeft, W, H, inset: 20);
        Assert.Equal(20, l);
        Assert.Equal(20, t);
    }

    // ── ComputeOverlapArea ──────────────────────────────────────────────────

    [Fact]
    public void ComputeOverlapArea_Overlapping_ReturnsCorrectArea()
    {
        var a = new Rect(0,   0, 100, 100);
        var b = new Rect(50, 50, 100, 100);
        Assert.Equal(2500, SnapHelper.ComputeOverlapArea(a, b));
    }

    [Fact]
    public void ComputeOverlapArea_NoOverlap_ReturnsZero()
    {
        var a = new Rect(0,   0, 100, 100);
        var b = new Rect(200, 0, 100, 100);
        Assert.Equal(0, SnapHelper.ComputeOverlapArea(a, b));
    }

    [Fact]
    public void ComputeOverlapArea_Touching_ReturnsZero()
    {
        var a = new Rect(0,   0, 100, 100);
        var b = new Rect(100, 0, 100, 100);
        Assert.Equal(0, SnapHelper.ComputeOverlapArea(a, b));
    }

    [Fact]
    public void ComputeOverlapArea_FullyContained_ReturnsInnerArea()
    {
        var a = new Rect(0,  0, 200, 200);
        var b = new Rect(50, 50,  50,  50);
        Assert.Equal(2500, SnapHelper.ComputeOverlapArea(a, b));
    }

    // ── Cross-screen scenario ────────────────────────────────────────────────

    [Fact]
    public void CrossScreen_FindBestWorkArea_ReturnsSecondary()
    {
        // Window at (2570, 10, 380, 590) — mostly on secondary
        var result = SnapHelper.FindBestWorkArea(TwoScreens, 2570, 10, W, H);
        Assert.Equal(Secondary, result);
    }

    [Fact]
    public void CrossScreen_DetectNearCorner_TopLeft_OnSecondary()
    {
        // Window near top-left of secondary
        var result = SnapHelper.DetectNearCorner(2570, 10, W, H, Secondary);
        Assert.Equal(SnapCorner.TopLeft, result);
    }
}
