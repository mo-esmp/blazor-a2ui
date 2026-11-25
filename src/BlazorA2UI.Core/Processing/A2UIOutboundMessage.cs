using System.Text.Json.Nodes;

namespace BlazorA2UI.Processing;

/// <summary>
/// A renderer-to-agent message (<c>action</c>, <c>callAgentFunction</c>,
/// <c>rendererFunctionResponse</c> or <c>error</c>) and the transport metadata to send with it.
/// </summary>
/// <param name="Message">The message envelope, including <c>"version": "1.0"</c>.</param>
/// <param name="Metadata">
/// Transport metadata. Holds <c>a2uiRendererDataModel</c> when a surface asked for
/// <c>sendDataModel</c>; null otherwise.
/// </param>
public sealed record A2UIOutboundMessage(JsonObject Message, JsonObject? Metadata)
{
    /// <summary>The message kind: the envelope's single non-version key.</summary>
    public string Kind => Message.Select(p => p.Key).First(k => k != "version");

    /// <summary>The message body under <see cref="Kind"/>.</summary>
    public JsonObject Body => (JsonObject)Message[Kind]!;
}

/// <summary>An error the renderer reported (and sent to the agent when an outbound channel exists).</summary>
public sealed record A2UIErrorReport(string Code, string Message, string? SurfaceId, string? FunctionCallId, string? Path);
