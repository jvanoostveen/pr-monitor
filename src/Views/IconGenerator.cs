using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace PrBot.Views;

/// <summary>
/// Generates tray icons dynamically: a base circle with an overlay badge count
/// and colour coding based on CI state.
/// </summary>
public static class IconGenerator
{
    private static readonly Color ColorCircle = Color.FromArgb(0, 95, 170);   // #005FAA – project blue

    /// <summary>
    /// Create a 16×16 icon with a coloured circle and optional badge count.
    /// </summary>
    public static Icon CreateTrayIcon(int totalCount, int failedCICount, int reviewCount)
    {
        // Use the system-recommended small icon size (DPI-aware: 16, 20, 24, 32 …)
        int size = SystemInformation.SmallIconSize.Width;

        using var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Color.Transparent);

        // Filled circle – always project blue
        using var brush = new SolidBrush(ColorCircle);
        g.FillEllipse(brush, 0, 0, size - 1, size - 1);

        // Badge number in white
        if (totalCount > 0)
        {
            var text = totalCount > 99 ? "…" : totalCount.ToString();
            float fontSize = totalCount > 9 ? size * 0.40f : size * 0.48f;
            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Point);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, Brushes.White, new RectangleF(0, 0, size, size), sf);
        }

        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }
}
