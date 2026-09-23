using System.Text.Json.Serialization;

namespace PrMonitor.Models;

/// <summary>Effect of a label on the PR's place in its section.</summary>
public enum LabelPriority
{
    /// <summary>Chip only; the PR keeps its place.</summary>
    None,

    /// <summary>Accent bar on the row and sorted to the top of its section.</summary>
    High,

    /// <summary>Sorted to the bottom of its section.</summary>
    Low,
}

/// <summary>
/// Maps a GitHub label to a chip on the PR row, optionally raising or lowering the PR's priority.
/// </summary>
public sealed class LabelRule
{
    /// <summary>GitHub label name, matched case-insensitively.</summary>
    public string Label { get; set; } = "";

    /// <summary>Chip text. Empty means the label name is shown.</summary>
    public string Text { get; set; } = "";

    /// <summary>Chip colour as "#RRGGBB". Empty means the chip follows the row's CI status colour.</summary>
    public string Color { get; set; } = "";

    [JsonConverter(typeof(TolerantLabelPriorityConverter))]
    public LabelPriority Priority { get; set; }

    /// <summary>
    /// Read-only migration of the earlier <c>isPriority</c> checkbox: <c>true</c> becomes <see cref="LabelPriority.High"/>.
    /// Never written back (null is skipped on save).
    /// </summary>
    [JsonInclude, JsonPropertyName("isPriority")]
    private bool? LegacyIsPriority
    {
        get => null;
        set
        {
            if (value == true && Priority == LabelPriority.None)
                Priority = LabelPriority.High;
        }
    }

    /// <summary>Muted grey that stays unobtrusive on both the dark and the light theme.</summary>
    public const string MutedGrey = "#8B949E";

    /// <summary>
    /// The rules that ship by default: 'Prioriteit/High' marks a PR as high priority,
    /// 'Prioriteit/Low' as low priority with a muted grey chip.
    /// </summary>
    public static List<LabelRule> DefaultRules() =>
    [
        new() { Label = "Prioriteit/High", Text = "HIGH", Priority = LabelPriority.High },
        new() { Label = "Prioriteit/Low", Text = "LOW", Color = MutedGrey, Priority = LabelPriority.Low },
    ];

    /// <summary>True when <paramref name="value"/> is a "#RRGGBB" colour.</summary>
    public static bool IsValidColor(string? value) =>
        value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);
}

/// <summary>
/// Reads <see cref="LabelPriority"/> by name (case-insensitive) or number; anything unrecognised
/// becomes <see cref="LabelPriority.None"/> instead of failing the whole settings file.
/// </summary>
internal sealed class TolerantLabelPriorityConverter : JsonConverter<LabelPriority>
{
    public override LabelPriority Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType == System.Text.Json.JsonTokenType.String
            && Enum.TryParse<LabelPriority>(reader.GetString(), ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed))
            return parsed;
        if (reader.TokenType == System.Text.Json.JsonTokenType.Number
            && reader.TryGetInt32(out var number)
            && Enum.IsDefined((LabelPriority)number))
            return (LabelPriority)number;
        return LabelPriority.None;
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, LabelPriority value, System.Text.Json.JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
