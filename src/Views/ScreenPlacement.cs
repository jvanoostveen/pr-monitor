using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using WinForms = System.Windows.Forms;

namespace PrMonitor;

/// <summary>
/// Monitor lookups for placing a WPF window, in the window's device-independent units.
/// The geometry itself lives in <see cref="SnapHelper"/>, which is unit-tested.
/// </summary>
internal static class ScreenPlacement
{
    /// <summary>Converts a physical-pixel screen rect to WPF units of <paramref name="visual"/>'s DPI.</summary>
    public static Rect ToWpf(Visual visual, System.Drawing.Rectangle r)
    {
        var source = PresentationSource.FromVisual(visual);
        if (source?.CompositionTarget == null)
            return new Rect(r.X, r.Y, r.Width, r.Height);
        var m = source.CompositionTarget.TransformFromDevice;
        var tl = m.Transform(new System.Windows.Point(r.Left, r.Top));
        var br = m.Transform(new System.Windows.Point(r.Right, r.Bottom));
        return new Rect(tl, br);
    }

    /// <summary>
    /// Work area of whichever monitor <paramref name="window"/> is on; the primary monitor
    /// while its handle isn't created yet.
    /// </summary>
    public static Rect CurrentWorkArea(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var screen = handle != IntPtr.Zero
            ? WinForms.Screen.FromHandle(handle)
            : WinForms.Screen.PrimaryScreen!;
        return ToWpf(window, screen.WorkingArea);
    }

    /// <inheritdoc cref="SnapHelper.ClampToBestWorkArea"/>
    public static (double left, double top) ClampToBestWorkArea(
        Visual visual, double left, double top, double width, double height) =>
        SnapHelper.ClampToBestWorkArea(
            WorkAreas(visual, WinForms.Screen.AllScreens),
            ToWpf(visual, WinForms.Screen.PrimaryScreen!.WorkingArea),
            left, top, width, height);

    /// <summary>
    /// The screen whose work area overlaps most with the given rect; the closest one (by center
    /// distance) when none overlaps, and the primary screen when there are none.
    /// </summary>
    public static WinForms.Screen FindBestScreen(Visual visual, double left, double top, double width, double height)
    {
        var screens = WinForms.Screen.AllScreens;
        var index = SnapHelper.FindBestWorkAreaIndex(WorkAreas(visual, screens), left, top, width, height);
        return index >= 0 ? screens[index] : WinForms.Screen.PrimaryScreen!;
    }

    private static List<Rect> WorkAreas(Visual visual, IEnumerable<WinForms.Screen> screens) =>
        screens.Select(s => ToWpf(visual, s.WorkingArea)).ToList();
}
