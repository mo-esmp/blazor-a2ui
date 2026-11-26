using BlazorA2UI.Evaluation;
using BlazorA2UI.Processing;

namespace BlazorA2UI.Agent.AspNetCore;

/// <summary>
/// A client's request to the agent endpoint, checked before anything runs:
/// <c>{"text": "...", "messages": [...], "metadata": {...}}</c>, where every part is optional but a request holds text
/// or messages.
/// </summary>
/// <param name="Text">The user's text, or null.</param>
/// <param name="Messages">The renderer's messages, each with a valid envelope.</param>
/// <param name="Metadata">The <c>metadata</c> object, or null.</param>
/// <param name="StartsTurn">Whether the request starts an agent turn: it has text, an action or an error.</param>
internal sealed record A2UIAgentRequest(string? Text, IReadOnlyList<JsonObject> Messages, JsonObject? Metadata, bool StartsTurn)
{
    /// <summary>
    /// Reads a request from its body.
    /// </summary>
    /// <exception cref="Microsoft.AspNetCore.Http.BadHttpRequestException">The request is malformed.</exception>
    /// <returns>The request.</returns>
    /// <param name="body">The body.</param>
    /// <param name="maxMessages">The most messages a request may carry.</param>
    public static A2UIAgentRequest Read(JsonObject body, int maxMessages)
    {
        string? text = null;
        JsonArray? messages = null;
        JsonObject? metadata = null;
        foreach ((string key, JsonNode? value) in body)
        {
            switch (key)
            {
                case "text" when value is null:
                case "messages" when value is null:
                case "metadata" when value is null:
                    break;
                case "text":
                    text = A2UIValues.AsString(value) ?? throw A2UIAgentEndpoint.Bad("'text' must be a string.");
                    break;
                case "messages":
                    messages = value as JsonArray ?? throw A2UIAgentEndpoint.Bad("'messages' must be an array.");
                    break;
                case "metadata":
                    metadata = value as JsonObject ?? throw A2UIAgentEndpoint.Bad("'metadata' must be an object.");
                    break;
                default:
                    throw A2UIAgentEndpoint.Bad($"The body has the unknown property '{Truncate(key)}'. It may hold 'text', 'messages' and 'metadata'.");
            }
        }

        if (messages?.Count > maxMessages)
        {
            throw A2UIAgentEndpoint.Bad($"The request holds more than {maxMessages} messages.");
        }

        List<JsonObject> checkedMessages = [];
        bool startsTurn = text is not null;
        for (int i = 0; i < (messages?.Count ?? 0); i++)
        {
            // The loop bound guarantees messages is set.
            JsonObject message = messages![i] as JsonObject ?? throw A2UIAgentEndpoint.Bad($"Message {i + 1} must be a JSON object.");
            try
            {
                (string kind, JsonObject envelopeBody) = RendererMessageParser.ReadEnvelope(message);
                switch (kind)
                {
                    case RendererMessageParser.Action:
                        _ = RendererMessageParser.ReadAction(envelopeBody);
                        startsTurn = true;
                        break;
                    case RendererMessageParser.Error:
                        _ = RendererMessageParser.ReadError(envelopeBody);
                        startsTurn = true;
                        break;
                }
            }
            catch (A2UIValidationException ex)
            {
                throw A2UIAgentEndpoint.Bad($"Message {i + 1} is not a valid renderer message: {ex.Message}");
            }

            checkedMessages.Add(message);
        }

        if (text is null && checkedMessages.Count == 0)
        {
            throw A2UIAgentEndpoint.Bad("The request has no text and no messages.");
        }

        return new A2UIAgentRequest(text, checkedMessages, metadata, startsTurn);
    }

    /// <summary>
    /// Shortens a client's value before it is echoed in an error.
    /// </summary>
    /// <param name="value">The value.</param>
    private static string Truncate(string value) => value.Length <= 64 ? value : value[..64] + "…";
}
