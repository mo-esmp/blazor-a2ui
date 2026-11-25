using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BlazorA2UI.Evaluation;

/// <summary>Coercion rules between JSON values, as defined by A2UI v1.0.</summary>
public static class A2UIValues
{
    private static readonly JsonSerializerOptions s_compactJson = new() { WriteIndented = false };

    /// <summary>
    /// <c>DynamicBoolean</c> coercion: booleans unchanged, <c>ValidationResult</c> to its <c>valid</c>,
    /// <c>"true"</c>/<c>"false"</c> in any case, non-zero numbers true, everything else false.
    /// </summary>
    public static bool ToBoolean(JsonNode? value)
    {
        switch (value)
        {
            case null:
                return false;
            case JsonObject obj:
                return IsValidationResult(obj) && obj["valid"]!.GetValue<bool>();
            case JsonArray:
                return false;
            case JsonValue v:
                switch (v.GetValueKind())
                {
                    case JsonValueKind.True:
                        return true;
                    case JsonValueKind.False:
                        return false;
                    case JsonValueKind.String:
                        string s = v.GetValue<string>();
                        return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
                    case JsonValueKind.Number:
                        double? d = ToDouble(v);
                        return d is { } n && n != 0 && !double.IsNaN(n);
                    default:
                        return false;
                }

            default:
                return false;
        }
    }

    /// <summary>True when <paramref name="obj"/> has a boolean <c>valid</c> property.</summary>
    public static bool IsValidationResult(JsonObject obj) =>
        obj["valid"] is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False;

    /// <summary>String conversion for interpolation and display: null is empty, objects and arrays are JSON.</summary>
    public static string ToDisplayString(JsonNode? value)
    {
        switch (value)
        {
            case null:
                return string.Empty;
            case JsonValue v:
                switch (v.GetValueKind())
                {
                    case JsonValueKind.String:
                        return v.GetValue<string>();
                    case JsonValueKind.True:
                        return "true";
                    case JsonValueKind.False:
                        return "false";
                    case JsonValueKind.Number:
                        return FormatNumber(ToDouble(v) ?? 0);
                    case JsonValueKind.Null:
                        return string.Empty;
                }

                break;
        }

        return value.ToJsonString(s_compactJson);
    }

    /// <summary>Formats a number the way JavaScript's <c>String(n)</c> does for common values.</summary>
    public static string FormatNumber(double n)
    {
        if (double.IsNaN(n))
        {
            return "NaN";
        }

        if (double.IsInfinity(n))
        {
            return n > 0 ? "Infinity" : "-Infinity";
        }

        if (n == Math.Floor(n) && Math.Abs(n) < 1e21)
        {
            return ((decimal)n).ToString(CultureInfo.InvariantCulture);
        }

        return n.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>Reads a number, accepting numeric strings. Returns null when the value is not numeric.</summary>
    public static double? ToDouble(JsonNode? value)
    {
        if (value is not JsonValue v)
        {
            return null;
        }

        switch (v.GetValueKind())
        {
            case JsonValueKind.Number:
                if (v.TryGetValue<double>(out double d))
                {
                    return d;
                }

                if (v.TryGetValue<int>(out int i))
                {
                    return i;
                }

                if (v.TryGetValue<long>(out long l))
                {
                    return l;
                }

                if (v.TryGetValue<decimal>(out decimal m))
                {
                    return (double)m;
                }

                return double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture);
            case JsonValueKind.String:
                string s = v.GetValue<string>().Trim();
                return s.Length > 0 && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                    ? parsed
                    : null;
            default:
                return null;
        }
    }

    /// <summary>Reads a string value, or null when the value is not a JSON string.</summary>
    public static string? AsString(JsonNode? value) =>
        value is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>Builds a <c>ValidationResult</c> object.</summary>
    public static JsonObject ValidationResult(bool valid, string? message = null, string? code = null, string? severity = null)
    {
        JsonObject result = new() { ["valid"] = valid };
        if (code is not null)
        {
            result["code"] = code;
        }

        if (message is not null)
        {
            result["message"] = message;
        }

        if (severity is not null)
        {
            result["severity"] = severity;
        }

        return result;
    }

    /// <summary>
    /// <c>CheckRule</c> coercion: a boolean becomes a <c>ValidationResult</c>. A failing result takes
    /// the rule's <paramref name="ruleMessage"/> when the rule has one (else keeps its own message),
    /// and its severity defaults to <c>error</c>.
    /// </summary>
    public static JsonObject ToValidationResult(JsonNode? value, string? ruleMessage)
    {
        if (value is JsonObject obj && IsValidationResult(obj))
        {
            JsonObject result = (JsonObject)obj.DeepClone();
            if (!obj["valid"]!.GetValue<bool>())
            {
                if (ruleMessage is not null)
                {
                    result["message"] = ruleMessage;
                }

                result["severity"] ??= "error";
            }

            return result;
        }

        return ToBoolean(value)
            ? new JsonObject { ["valid"] = true }
            : ValidationResult(false, ruleMessage, severity: "error");
    }
}
