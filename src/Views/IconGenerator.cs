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
    private static readonly Color ColorRed    = Color.FromArgb(0xF8, 0x51, 0x49); // #F85149 – CI failure
    private static readonly Color ColorAmber  = Color.FromArgb(0xD2, 0x99, 0x22); // #D29922 – reviews pending
    private static readonly Color ColorGreen  = Color.FromArgb(0x3F, 0xB9, 0x50); // #3FB950 – all clear
    private static readonly Color ColorGray   = Color.FromArgb(0x8B, 0x94, 0x9E); // #8B949E – idle / not polled

    /// <summary>
    /// Create a 16×16 icon with a coloured circle and optional badge count.
    /// Circle colour reflects worst state: red (CI failure) > amber (review) > green (all OK) > gray (idle).
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

        // Pick circle colour based on worst state
        Color circleColor = failedCICount > 0 ? ColorRed
                          : reviewCount   > 0 ? ColorAmber
                          : totalCount    > 0 ? ColorGreen
                                              : ColorGray;

        using var brush = new SolidBrush(circleColor);
        g.FillEllipse(brush, 0, 0, size - 1, size - 1);

        // Badge number – pick black or white based on background luminance for best contrast
        if (totalCount > 0)
        {
            var text = totalCount > 99 ? "…" : totalCount.ToString();
            float fontSize = totalCount > 9 ? size * 0.40f : size * 0.48f;
            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Point);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            var textBrush = GetContrastBrush(circleColor);
            g.DrawString(text, font, textBrush, new RectangleF(0, 0, size, size), sf);
        }

        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }

    /// <summary>
    /// Returns black or white brush, whichever has higher contrast against <paramref name="background"/>.
    /// Uses WCAG relative-luminance formula.
    /// </summary>
    private static Brush GetContrastBrush(Color background)
    {
        static double Linearize(double c)
        {
            c /= 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        double L = 0.2126 * Linearize(background.R)
                 + 0.7152 * Linearize(background.G)
                 + 0.0722 * Linearize(background.B);

        // WCAG: contrast with white = (1+0.05)/(L+0.05), with black = (L+0.05)/(0+0.05)
        double contrastWithWhite = 1.05 / (L + 0.05);
        double contrastWithBlack = (L + 0.05) / 0.05;

        return contrastWithWhite >= contrastWithBlack ? Brushes.White : Brushes.Black;
    }
}
