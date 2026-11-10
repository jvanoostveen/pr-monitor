using System.Diagnostics;

namespace PrMonitor.Services;

/// <summary>Opens web pages in the user's default browser.</summary>
public static class Browser
{
    /// <summary>
    /// Opens <paramref name="url"/> when it is an absolute https URL and ignores anything else.
    /// URLs come from GitHub data, toasts and settings; shell-executing a file path or another
    /// scheme from there would start a program instead of a web page.
    /// </summary>
    public static void OpenHttps(string? url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
