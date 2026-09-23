namespace PrMonitor.Models;

/// <summary>
/// Maps a GitHub label to a chip on the PR row, optionally marking the PR as priority.
/// </summary>
public sealed class LabelRule
{
    /// <summary>GitHub label name, matched case-insensitively.</summary>
    public string Label { get; set; } = "";

    /// <summary>Chip text. Empty means the label name is shown.</summary>
    public string Text { get; set; } = "";

    /// <summary>Chip colour as "#RRGGBB". Empty means the chip follows the row's CI status colour.</summary>
    public string Color { get; set; } = "";

    /// <summary>Priority PRs get a coloured accent bar and are sorted to the top of their section.</summary>
    public bool IsPriority { get; set; }

    /// <summary>The rule that ships by default: 'Prioriteit/High' marks a PR as priority.</summary>
    public static LabelRule DefaultPriority() => new()
    {
        Label = "Prioriteit/High",
        Text = "HIGH",
        IsPriority = true,
    };

    /// <summary>True when <paramref name="value"/> is a "#RRGGBB" colour.</summary>
    public static bool IsValidColor(string? value) =>
        value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);
}
