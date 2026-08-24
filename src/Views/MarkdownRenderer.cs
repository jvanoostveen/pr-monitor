using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace PrMonitor.Views;

/// <summary>
/// Minimal markdown-to-FlowDocument renderer covering the subset used by CHANGELOG.md:
/// ## / ### headings, "- " bullets, and inline **bold** / `code` spans.
/// </summary>
internal static class MarkdownRenderer
{
    private static readonly Regex InlineTokenRegex = new(@"\*\*(.+?)\*\*|`([^`]+)`", RegexOptions.Compiled);

    private static readonly SolidColorBrush HeadingBrush = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE6, 0xED, 0xF3)));
    private static readonly SolidColorBrush BodyBrush = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC9, 0xD1, 0xD9)));
    private static readonly SolidColorBrush BulletBrush = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8B, 0x94, 0x9E)));
    private static readonly SolidColorBrush CodeForeground = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF0, 0x88, 0x3E)));
    private static readonly SolidColorBrush CodeBackground = Freeze(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x30, 0x36, 0x3D)));

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    public static FlowDocument ToFlowDocument(string markdown)
    {
        var document = new FlowDocument
        {
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 13,
            Foreground = BodyBrush,
            PagePadding = new Thickness(0),
        };

        foreach (var rawLine in (markdown ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("## "))
            {
                document.Blocks.Add(CreateHeading(trimmed[3..].Trim(), fontSize: 17, topMargin: 18));
            }
            else if (trimmed.StartsWith("### "))
            {
                document.Blocks.Add(CreateHeading(trimmed[4..].Trim(), fontSize: 14, topMargin: 14));
            }
            else if (trimmed.StartsWith("- "))
            {
                var indent = (line.Length - trimmed.Length) / 2;
                document.Blocks.Add(CreateBullet(trimmed[2..], indent));
            }
            else
            {
                document.Blocks.Add(CreateParagraph(line, new Thickness(0, 2, 0, 2)));
            }
        }

        return document;
    }

    private static Paragraph CreateHeading(string text, double fontSize, double topMargin)
    {
        var paragraph = new Paragraph
        {
            FontSize = fontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = HeadingBrush,
            Margin = new Thickness(0, topMargin, 0, 6),
        };
        AddInlines(paragraph.Inlines, text);
        return paragraph;
    }

    private static Paragraph CreateBullet(string text, int indentLevel)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(14 + (indentLevel * 14), 2, 0, 2),
            TextIndent = -14,
        };
        paragraph.Inlines.Add(new Run("• ") { Foreground = BulletBrush });
        AddInlines(paragraph.Inlines, text);
        return paragraph;
    }

    private static Paragraph CreateParagraph(string text, Thickness margin)
    {
        var paragraph = new Paragraph { Margin = margin };
        AddInlines(paragraph.Inlines, text);
        return paragraph;
    }

    private static void AddInlines(InlineCollection inlines, string text)
    {
        var pos = 0;
        while (pos < text.Length)
        {
            var match = InlineTokenRegex.Match(text, pos);
            if (!match.Success)
            {
                inlines.Add(new Run(text[pos..]));
                break;
            }

            if (match.Index > pos)
                inlines.Add(new Run(text[pos..match.Index]));

            if (match.Groups[1].Success)
            {
                inlines.Add(new Run(match.Groups[1].Value) { FontWeight = FontWeights.Bold, Foreground = HeadingBrush });
            }
            else
            {
                inlines.Add(new Run(match.Groups[2].Value)
                {
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    Foreground = CodeForeground,
                    Background = CodeBackground,
                });
            }

            pos = match.Index + match.Length;
        }
    }
}
