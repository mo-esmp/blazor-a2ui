using System.Text;
using System.Text.Json;

namespace BlazorA2UI.Agent;

/// <summary>
/// Reads the JSON a model writes, which is often almost JSON: it accepts trailing commas, comments, raw line breaks and
/// tabs inside strings, and curly double quotes used as JSON quotes.
/// </summary>
internal static class LenientJson
{
    /// <summary>
    /// The reader options: trailing commas and comments are allowed, duplicate keys are not, and nesting is bounded.
    /// </summary>
    private static readonly JsonDocumentOptions s_options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        AllowDuplicateProperties = false,
        MaxDepth = JsonChecks.MaxDepth + 2,
    };

    /// <summary>
    /// Reads one JSON value.
    /// </summary>
    /// <returns>True when the text reads as JSON, possibly after repair.</returns>
    /// <param name="text">The text.</param>
    /// <param name="node">Set to the value.</param>
    /// <param name="error">Set to why the text is not JSON, from the first attempt, when it is not.</param>
    public static bool TryParse(string text, out JsonNode? node, out string? error)
    {
        if (TryParseExact(text, out node, out error))
        {
            return true;
        }

        string escaped = EscapeControlCharacters(text);
        if (!ReferenceEquals(escaped, text) && TryParseExact(escaped, out node, out _))
        {
            error = null;
            return true;
        }

        if (text.Contains('“') || text.Contains('”'))
        {
            string straight = EscapeControlCharacters(text.Replace('“', '"').Replace('”', '"'));
            if (TryParseExact(straight, out node, out _))
            {
                error = null;
                return true;
            }
        }

        node = null;
        return false;
    }

    /// <summary>
    /// Reads one JSON value without repair.
    /// </summary>
    /// <returns>True when the text is JSON.</returns>
    /// <param name="text">The text.</param>
    /// <param name="node">Set to the value.</param>
    /// <param name="error">Set to why the text is not JSON.</param>
    private static bool TryParseExact(string text, out JsonNode? node, out string? error)
    {
        try
        {
            node = JsonNode.Parse(text, documentOptions: s_options);
            if (!HasValidStrings(node))
            {
                node = null;
                error = "The JSON has a string that is not valid Unicode, such as an unpaired surrogate escape.";
                return false;
            }

            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            node = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Returns whether every key and string in the JSON can be read. The parser checks escapes only lazily, so an
    /// unpaired surrogate such as <c>\ud800</c> would otherwise throw later, when a caller reads it.
    /// </summary>
    /// <param name="node">The parsed JSON.</param>
    private static bool HasValidStrings(JsonNode? node)
    {
        try
        {
            Visit(node);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads every key and string under a node, so a string that cannot be decoded throws here. The depth is bounded
    /// by the parser's <see cref="JsonDocumentOptions.MaxDepth"/>.
    /// </summary>
    /// <param name="node">The node.</param>
    private static void Visit(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach ((string key, JsonNode? value) in obj)
                {
                    _ = key.Length;
                    Visit(value);
                }

                break;
            case JsonArray array:
                foreach (JsonNode? item in array)
                {
                    Visit(item);
                }

                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                _ = value.GetValue<string>();
                break;
        }
    }

    /// <summary>
    /// Escapes raw control characters (line breaks, tabs and the like) inside JSON strings.
    /// </summary>
    /// <returns>The escaped text, or <paramref name="text"/> itself when nothing needed escaping.</returns>
    /// <param name="text">The text.</param>
    private static string EscapeControlCharacters(string text)
    {
        StringBuilder? result = null;
        bool inString = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inString && c == '\\' && i + 1 < text.Length)
            {
                result?.Append(c).Append(text[i + 1]);
                i++;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
            }
            else if (inString && c < ' ')
            {
                result ??= new StringBuilder(text, 0, i, text.Length + 16);
                result.Append(c switch
                {
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    _ => "\\u" + ((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture),
                });
                continue;
            }

            result?.Append(c);
        }

        return result?.ToString() ?? text;
    }
}
