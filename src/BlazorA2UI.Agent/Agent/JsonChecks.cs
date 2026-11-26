namespace BlazorA2UI.Agent;

/// <summary>
/// Checks on JSON values that come from the renderer or go to it.
/// </summary>
internal static class JsonChecks
{
    /// <summary>
    /// The deepest value a message may hold, counted from the envelope at depth 0: the renderer's
    /// <see cref="Processing.MessageProcessor.MaxMessageDepth"/>, so a message that passes here passes there too.
    /// </summary>
    internal const int MaxDepth = Processing.MessageProcessor.MaxMessageDepth;

    /// <summary>
    /// Returns whether a whole message can go on the wire: every number is finite and nothing is nested deeper than
    /// <see cref="MaxDepth"/> below the envelope.
    /// </summary>
    /// <param name="message">The message, with its envelope.</param>
    public static bool IsSendable(JsonObject message) => IsSendable(message, 0);

    /// <summary>
    /// Returns whether a value at a given depth can go on the wire.
    /// </summary>
    /// <param name="node">The value.</param>
    /// <param name="depth">How deep the value is nested.</param>
    private static bool IsSendable(JsonNode? node, int depth)
    {
        if (depth > MaxDepth)
        {
            return false;
        }

        switch (node)
        {
            case JsonObject obj:
                foreach ((string _, JsonNode? item) in obj)
                {
                    if (!IsSendable(item, depth + 1))
                    {
                        return false;
                    }
                }

                return true;
            case JsonArray array:
                foreach (JsonNode? item in array)
                {
                    if (!IsSendable(item, depth + 1))
                    {
                        return false;
                    }
                }

                return true;
            case JsonValue value when value.TryGetValue(out double number):
                return double.IsFinite(number);
            case JsonValue value when value.TryGetValue(out float single):
                return float.IsFinite(single);
            default:
                return true;
        }
    }

    /// <summary>
    /// Returns a value ready to be placed in a message: the value itself when it has no parent, or a copy.
    /// </summary>
    /// <param name="node">The value.</param>
    public static JsonNode? Detach(JsonNode? node) => node?.Parent is null ? node : node.DeepClone();
}
