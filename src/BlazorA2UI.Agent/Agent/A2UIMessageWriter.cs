using System.Buffers;
using System.Text.Json;

namespace BlazorA2UI.Agent;

/// <summary>
/// Writes messages to a stream as JSON Lines: one compact JSON object per line, flushed after each message so the
/// renderer can draw it at once. JSON escapes line breaks inside strings, so a message never spans two lines.
/// </summary>
/// <remarks>
/// The writer does not own the stream, and it is not safe for concurrent use: write one message at a time.
/// </remarks>
public sealed class A2UIMessageWriter
{
    /// <summary>
    /// The JSON Lines media type.
    /// </summary>
    public const string MediaType = "application/jsonl";

    /// <summary>
    /// The stream messages go to.
    /// </summary>
    private readonly Stream _stream;

    /// <summary>
    /// The buffer one message is serialized into before it is written.
    /// </summary>
    private readonly ArrayBufferWriter<byte> _buffer = new(1024);

    /// <summary>
    /// Creates a writer.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> is not writable.</exception>
    /// <param name="stream">The stream to write to.</param>
    public A2UIMessageWriter(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("The stream is not writable.", nameof(stream));
        }

        _stream = stream;
    }

    /// <summary>
    /// Writes one message as a line and flushes the stream.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    /// <param name="message">The message envelope.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async ValueTask WriteAsync(JsonObject message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        _buffer.ResetWrittenCount();
        using (Utf8JsonWriter json = new(_buffer))
        {
            message.WriteTo(json);
        }

        _buffer.Write("\n"u8);
        await _stream.WriteAsync(_buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
