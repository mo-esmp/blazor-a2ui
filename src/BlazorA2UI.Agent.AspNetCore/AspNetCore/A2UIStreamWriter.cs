using System.Buffers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace BlazorA2UI.Agent.AspNetCore;

/// <summary>
/// Writes the agent's side of the response: JSON Lines with one A2UI message per line, or server-sent events that also
/// carry text and errors. Each write is flushed at once, and writes from several tasks are serialized.
/// </summary>
internal sealed class A2UIStreamWriter
{
    /// <summary>
    /// The media type of server-sent events.
    /// </summary>
    internal const string EventStreamMediaType = "text/event-stream";

    /// <summary>
    /// The response body.
    /// </summary>
    private readonly Stream _body;

    /// <summary>
    /// Serializes writes, so frames never interleave.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Holds a frame while it is written.
    /// </summary>
    private readonly ArrayBufferWriter<byte> _buffer = new(1024);

    /// <summary>
    /// Creates a writer for a response body.
    /// </summary>
    /// <param name="body">The response body.</param>
    /// <param name="eventStream">Whether to write server-sent events instead of JSON Lines.</param>
    public A2UIStreamWriter(Stream body, bool eventStream)
    {
        _body = body;
        IsEventStream = eventStream;
    }

    /// <summary>
    /// Gets whether the response is server-sent events, which can carry text and errors as well as messages.
    /// </summary>
    public bool IsEventStream { get; }

    /// <summary>
    /// Gets the response's media type.
    /// </summary>
    public string MediaType => IsEventStream ? EventStreamMediaType : A2UIMessageWriter.MediaType;

    /// <summary>
    /// Returns whether a request prefers server-sent events in its <c>Accept</c> header: it names them with a quality
    /// above zero, and at least as high as JSON Lines gets, by name or through a wildcard.
    /// </summary>
    /// <param name="request">The request.</param>
    public static bool WantsEventStream(HttpRequest request)
    {
        double eventStream = 0;
        double jsonLines = 0;
        foreach (MediaTypeHeaderValue accept in request.GetTypedHeaders().Accept)
        {
            double quality = accept.Quality ?? 1;
            string? mediaType = accept.MediaType.Value;
            if (string.Equals(mediaType, EventStreamMediaType, StringComparison.OrdinalIgnoreCase))
            {
                eventStream = Math.Max(eventStream, quality);
            }
            else if (string.Equals(mediaType, A2UIMessageWriter.MediaType, StringComparison.OrdinalIgnoreCase)
                || mediaType is "*/*" || string.Equals(mediaType, "application/*", StringComparison.OrdinalIgnoreCase))
            {
                jsonLines = Math.Max(jsonLines, quality);
            }
        }

        return eventStream > 0 && eventStream >= jsonLines;
    }

    /// <summary>
    /// Writes an A2UI message.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public Task WriteMessageAsync(JsonObject message, CancellationToken cancellationToken) =>
        WriteAsync("a2ui", message, cancellationToken);

    /// <summary>
    /// Writes text for the user as a <c>text</c> event. JSON Lines cannot carry text, so it is not written there.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public Task WriteTextAsync(string text, CancellationToken cancellationToken) =>
        IsEventStream ? WriteAsync("text", new JsonObject { ["text"] = text }, cancellationToken) : Task.CompletedTask;

    /// <summary>
    /// Writes an <c>error</c> event that ends the turn. JSON Lines cannot carry it, so it is not written there.
    /// </summary>
    /// <param name="message">The message for the client. It never holds exception details.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public Task WriteErrorAsync(string message, CancellationToken cancellationToken) =>
        IsEventStream ? WriteAsync("error", new JsonObject { ["message"] = message }, cancellationToken) : Task.CompletedTask;

    /// <summary>
    /// Writes one frame and flushes it.
    /// </summary>
    /// <param name="eventName">The event's name, used for server-sent events.</param>
    /// <param name="payload">The JSON of the frame.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    private async Task WriteAsync(string eventName, JsonObject payload, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _buffer.ResetWrittenCount();
            if (IsEventStream)
            {
                _buffer.Write("event: "u8);
                _buffer.Write(System.Text.Encoding.UTF8.GetBytes(eventName));
                _buffer.Write("\ndata: "u8);
            }

            // Compact JSON escapes every line break, so a frame is always one line.
            using (Utf8JsonWriter json = new(_buffer))
            {
                payload.WriteTo(json);
            }

            _buffer.Write(IsEventStream ? "\n\n"u8 : "\n"u8);
            await _body.WriteAsync(_buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
            await _body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
