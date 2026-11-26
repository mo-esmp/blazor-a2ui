using System.Text;

namespace BlazorA2UI.Agent;

/// <summary>
/// Reads a model's complete answer into text and A2UI messages. The answer holds A2UI JSON blocks between
/// <see cref="OpenTag"/> and <see cref="CloseTag"/>, as <see cref="A2UIPromptBuilder"/> asks for, each holding a JSON
/// array of messages.
/// </summary>
/// <remarks>
/// The reader forgives what models commonly get wrong: tags in another case or with attributes, Markdown fences inside
/// or around a block, a single message instead of an array, trailing commas, comments, raw line breaks in strings, curly
/// quotes, and a missing <c>version</c>, which is set to 1.0. A close tag inside a JSON string does not end a block. It
/// never throws for the model's text: a block it cannot read becomes an <see cref="A2UIErrorPart"/>. To read an answer
/// as it streams, use <see cref="A2UIResponseStream"/>.
/// </remarks>
public static class A2UIResponseParser
{
    /// <summary>
    /// The tag that opens an A2UI JSON block.
    /// </summary>
    public const string OpenTag = "<a2ui-json>";

    /// <summary>
    /// The tag that closes an A2UI JSON block.
    /// </summary>
    public const string CloseTag = "</a2ui-json>";

    /// <summary>
    /// Reads a complete answer.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="response"/> is null.</exception>
    /// <returns>The parts in order, with the text between blocks in one part each.</returns>
    /// <param name="response">The model's answer.</param>
    /// <param name="options">The options, or null for the defaults.</param>
    public static IReadOnlyList<A2UIResponsePart> Parse(string response, A2UIResponseParserOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        A2UIResponseStream stream = new(options);
        List<A2UIResponsePart> parts = [.. stream.Append(response), .. stream.Complete()];
        return Merge(parts);
    }

    /// <summary>
    /// Reads JSON that holds only messages, with no tags around it: a JSON array of messages or a single message, as a
    /// model writes it in a tool call's arguments.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    /// <returns>The messages, and an error part if the JSON or a message could not be read.</returns>
    /// <param name="json">The JSON.</param>
    /// <param name="options">The options, or null for the defaults.</param>
    public static IReadOnlyList<A2UIResponsePart> ParseJson(string json, A2UIResponseParserOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        A2UIResponseStream stream = new(options, wrapped: false);
        return [.. stream.Append(json), .. stream.Complete()];
    }

    /// <summary>
    /// Joins consecutive text parts.
    /// </summary>
    /// <param name="parts">The parts.</param>
    private static List<A2UIResponsePart> Merge(List<A2UIResponsePart> parts)
    {
        List<A2UIResponsePart> merged = [];
        StringBuilder? text = null;
        foreach (A2UIResponsePart part in parts)
        {
            if (part is A2UITextPart piece)
            {
                (text ??= new StringBuilder()).Append(piece.Text);
                continue;
            }

            if (text is not null)
            {
                merged.Add(new A2UITextPart(text.ToString()));
                text = null;
            }

            merged.Add(part);
        }

        if (text is not null)
        {
            merged.Add(new A2UITextPart(text.ToString()));
        }

        return merged;
    }
}
