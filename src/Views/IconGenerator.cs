using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using PrBot.Models;

namespace PrBot.Views;

/// <summary>
/// Generates tray icons dynamically: a base circle with an overlay badge count
/// and colour coding based on CI state.
/// </summary>
public static class IconGenerator
{
    private static readonly Color ColorGreen = Color.FromArgb(63, 185, 80);   // #3FB950 – all good
    private static readonly Color ColorRed = Color.FromArgb(248, 81, 73);     // #F85149 – CI failure
    private static readonly Color ColorOrange = Color.FromArgb(210, 153, 34); // #D29922 – pending reviews
    private static readonly Color ColorGray = Color.FromArgb(139, 148, 158);  // #8B949E – idle / no items

    /// <summary>
    /// Create a 16×16 icon with a coloured circle and optional badge count.
    /// </summary>
    public static Icon CreateTrayIcon(int totalCount, int failedCICount, int reviewCount)
    {
        var colour = DetermineColour(failedCICount, reviewCount, totalCount);
        const int size = 16;

        using var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Color.Transparent);

        // Filled circle (slightly inset to leave room for outline)
        using var brush = new SolidBrush(colour);
        g.FillEllipse(brush, 1, 1, size - 3, size - 3);

        // White outline for contrast against any taskbar colour
        using var pen = new Pen(Color.White, 1.2f);
        g.DrawEllipse(pen, 1, 1, size - 3, size - 3);

        // Badge number
        if (totalCount > 0)
        {
            var text = totalCount > 99 ? "…" : totalCount.ToString();
            using var font = new Font("Segoe UI", totalCount > 9 ? 6.5f : 7.5f, FontStyle.Bold, GraphicsUnit.Point);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, Brushes.White, new RectangleF(0, 0, size, size), sf);
        }

        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }

    private static Color DetermineColour(int failedCI, int reviews, int total)
    {
        if (failedCI > 0) return ColorRed;
        if (reviews > 0) return ColorOrange;
        if (total > 0) return ColorGreen;
        return ColorGray;
    }
}
