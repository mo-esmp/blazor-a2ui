using System.Collections.Frozen;
using System.Globalization;

namespace BlazorA2UI.Rendering;

/// <summary>
/// Maps the basic catalog's layout enumerations to fixed CSS class names. A model value only ever selects one
/// of these names; a value outside an enumeration maps to nothing, so no model text reaches a class or a style.
/// </summary>
internal static class A2UIClasses
{
    /// <summary>
    /// The largest weight class; larger weights use it.
    /// </summary>
    internal const int MaxWeight = 12;

    /// <summary>
    /// <c>justify</c> of <c>Row</c> and <c>Column</c>.
    /// </summary>
    private static readonly FrozenDictionary<string, string> s_justify = Map(
        "a2ui-justify-", "start", "center", "end", "spaceBetween", "spaceAround", "spaceEvenly", "stretch");

    /// <summary>
    /// <c>align</c> of <c>Row</c>, <c>Column</c> and <c>List</c>.
    /// </summary>
    private static readonly FrozenDictionary<string, string> s_align = Map("a2ui-align-", "start", "center", "end", "stretch");

    /// <summary>
    /// <c>direction</c> of <c>List</c>.
    /// </summary>
    private static readonly FrozenDictionary<string, string> s_direction = Map("a2ui-direction-", "vertical", "horizontal");

    /// <summary>
    /// <c>variant</c> of <c>Text</c>.
    /// </summary>
    private static readonly FrozenDictionary<string, string> s_textVariant = Map("a2ui-text-", "caption", "body");

    /// <summary>
    /// <c>variant</c> of <c>Image</c>.
    /// </summary>
    private static readonly FrozenDictionary<string, string> s_imageVariant = Map(
        "a2ui-image-", "icon", "avatar", "smallFeature", "mediumFeature", "largeFeature", "header");

    /// <summary>
    /// <c>fit</c> of <c>Image</c>.
    /// </summary>
    private static readonly FrozenDictionary<string, string> s_fit = Map("a2ui-fit-", "contain", "cover", "fill", "none", "scaleDown");

    /// <summary>
    /// <c>variant</c> of <c>Button</c>.
    /// </summary>
    private static readonly FrozenDictionary<string, string> s_buttonVariant = Map("a2ui-button-", "default", "primary", "borderless");

    /// <summary>
    /// <c>axis</c> of <c>Divider</c>.
    /// </summary>
    private static readonly FrozenDictionary<string, string> s_axis = Map("a2ui-axis-", "horizontal", "vertical");

    /// <summary>
    /// <c>displayStyle</c> of <c>ChoicePicker</c>.
    /// </summary>
    private static readonly FrozenDictionary<string, string> s_displayStyle = Map("a2ui-choices-", "checkbox", "chips");

    /// <summary>
    /// The weight classes, by weight.
    /// </summary>
    private static readonly string[] s_weights = Enumerable.Range(0, MaxWeight + 1)
        .Select(w => "a2ui-weight-" + w.ToString(CultureInfo.InvariantCulture))
        .ToArray();

    /// <summary>
    /// The class for a <c>justify</c> value, or null when the value is not in the enumeration.
    /// </summary>
    internal static string? Justify(JsonNode? value) => Lookup(s_justify, value);

    /// <summary>
    /// The class for an <c>align</c> value, or null when the value is not in the enumeration.
    /// </summary>
    internal static string? Align(JsonNode? value) => Lookup(s_align, value);

    /// <summary>
    /// The class for a <c>List</c> <c>direction</c>, or null when the value is not in the enumeration.
    /// </summary>
    internal static string? Direction(JsonNode? value) => Lookup(s_direction, value);

    /// <summary>
    /// The class for a <c>Text</c> <c>variant</c>, or null when the value is not in the enumeration.
    /// </summary>
    internal static string? TextVariant(JsonNode? value) => Lookup(s_textVariant, value);

    /// <summary>
    /// The class for an <c>Image</c> <c>variant</c>, or null when the value is not in the enumeration.
    /// </summary>
    internal static string? ImageVariant(JsonNode? value) => Lookup(s_imageVariant, value);

    /// <summary>
    /// The class for an <c>Image</c> <c>fit</c>, or null when the value is not in the enumeration.
    /// </summary>
    internal static string? Fit(JsonNode? value) => Lookup(s_fit, value);

    /// <summary>
    /// The class for a <c>Button</c> <c>variant</c>, or null when the value is not in the enumeration.
    /// </summary>
    internal static string? ButtonVariant(JsonNode? value) => Lookup(s_buttonVariant, value);

    /// <summary>
    /// The class for a <c>Divider</c> <c>axis</c>, or null when the value is not in the enumeration.
    /// </summary>
    internal static string? Axis(JsonNode? value) => Lookup(s_axis, value);

    /// <summary>
    /// The class for a <c>ChoicePicker</c> <c>displayStyle</c>, or null when the value is not in the enumeration.
    /// </summary>
    internal static string? DisplayStyle(JsonNode? value) => Lookup(s_displayStyle, value);

    /// <summary>
    /// The class for a <c>weight</c>: the number rounded to a whole weight from 1 to <see cref="MaxWeight"/>,
    /// or null for a missing, non-numeric, non-finite or non-positive value.
    /// </summary>
    internal static string? Weight(JsonNode? value)
    {
        if (A2UIValues.ToDouble(value) is not { } weight || !double.IsFinite(weight) || weight <= 0)
        {
            return null;
        }

        int rounded = (int)Math.Clamp(Math.Round(weight, MidpointRounding.AwayFromZero), 1, MaxWeight);
        return s_weights[rounded];
    }

    /// <summary>
    /// Joins the non-null class names with spaces.
    /// </summary>
    internal static string Join(string first, string? second = null, string? third = null, string? fourth = null, string? fifth = null)
    {
        int length = first.Length + (second?.Length + 1 ?? 0) + (third?.Length + 1 ?? 0) + (fourth?.Length + 1 ?? 0) + (fifth?.Length + 1 ?? 0);
        return string.Create(length, (first, second, third, fourth, fifth), static (span, parts) =>
        {
            int at = 0;
            Append(span, ref at, parts.first);
            Append(span, ref at, parts.second);
            Append(span, ref at, parts.third);
            Append(span, ref at, parts.fourth);
            Append(span, ref at, parts.fifth);
        });
    }

    /// <summary>
    /// Writes one class name into <paramref name="span"/>, preceded by a space unless it is the first.
    /// </summary>
    private static void Append(Span<char> span, ref int at, string? part)
    {
        if (part is null)
        {
            return;
        }

        if (at > 0)
        {
            span[at++] = ' ';
        }

        part.AsSpan().CopyTo(span[at..]);
        at += part.Length;
    }

    /// <summary>
    /// Looks up a literal string value; dynamic values and values outside the enumeration give null.
    /// </summary>
    private static string? Lookup(FrozenDictionary<string, string> map, JsonNode? value) =>
        A2UIValues.AsString(value) is { } key && map.TryGetValue(key, out string? css) ? css : null;

    /// <summary>
    /// Builds an enumeration map whose class names are the prefix followed by the value in kebab case.
    /// </summary>
    private static FrozenDictionary<string, string> Map(string prefix, params string[] values) =>
        values.ToFrozenDictionary(v => v, v => prefix + Kebab(v), StringComparer.Ordinal);

    /// <summary>
    /// Converts a camel-case enumeration value to kebab case (<c>spaceBetween</c> to <c>space-between</c>).
    /// </summary>
    private static string Kebab(string value) =>
        string.Concat(value.Select((ch, i) => char.IsUpper(ch) && i > 0 ? "-" + char.ToLowerInvariant(ch) : ch.ToString()));
}
