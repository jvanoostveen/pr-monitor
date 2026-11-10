using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PrMonitor.Views;

/// <summary>Dark window title bar for the app's dialogs.</summary>
internal static class DarkTitleBar
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DwmwaUseImmersiveDarkMode = 20;

    /// <summary>
    /// Requests a dark title bar from DWM (Windows 10 1903+ / Windows 11). Call from
    /// <c>OnSourceInitialized</c>, once the window handle exists.
    /// </summary>
    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        int value = 1;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
    }
}
