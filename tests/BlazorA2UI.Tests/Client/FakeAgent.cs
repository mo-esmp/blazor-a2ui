using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Channels;

namespace BlazorA2UI.Tests.Client;

/// <summary>
/// An agent endpoint in memory: it records each request and answers with what the test returns, so the client is
/// tested without sockets.
/// </summary>
/// <param name="respond">Answers a request, given its parsed body.</param>
internal sealed class FakeAgent(Func<HttpRequestMessage, JsonObject, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    /// <summary>
    /// The endpoint's URL.
    /// </summary>
    public static readonly Uri Endpoint = new("https://agent.test/a2ui");

    /// <summary>
    /// Gets the requests received, with their parsed bodies, in order.
    /// </summary>
    public List<(HttpRequestMessage Request, JsonObject Body)> Requests { get; } = [];

    /// <summary>
    /// Builds a JSON Lines response with a conversation id.
    /// </summary>
    /// <returns>The response.</returns>
    /// <param name="body">The body.</param>
    /// <param name="conversationId">The id to send, or null.</param>
    public static HttpResponseMessage JsonLines(string body, string? conversationId = "conv1") =>
        Respond(new StringContent(body, Encoding.UTF8, "application/jsonl"), conversationId);

    /// <summary>
    /// Builds a response whose body the test writes as it goes.
    /// </summary>
    /// <returns>The response.</returns>
    /// <param name="body">The body the test writes to.</param>
    /// <param name="mediaType">The media type.</param>
    public static HttpResponseMessage Streaming(StreamingBody body, string mediaType = "application/jsonl") =>
        Respond(new StreamContent(body) { Headers = { ContentType = new MediaTypeHeaderValue(mediaType) } }, "conv1");

    /// <summary>
    /// Builds an RFC 9457 problem response.
    /// </summary>
    /// <returns>The response.</returns>
    /// <param name="status">The status code.</param>
    /// <param name="detail">The detail.</param>
    public static HttpResponseMessage Problem(HttpStatusCode status, string detail) =>
        new(status) { Content = new StringContent($$"""{"status":{{(int)status}},"detail":"{{detail}}"}""", Encoding.UTF8, "application/problem+json") };

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Every request the client sends has a JSON body.
        JsonObject body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
        lock (Requests)
        {
            Requests.Add((request, body));
        }

        return await respond(request, body);
    }

    /// <summary>
    /// Builds a 200 response.
    /// </summary>
    /// <returns>The response.</returns>
    /// <param name="content">The content.</param>
    /// <param name="conversationId">The id to send, or null.</param>
    private static HttpResponseMessage Respond(HttpContent content, string? conversationId)
    {
        HttpResponseMessage response = new(HttpStatusCode.OK) { Content = content };
        if (conversationId is not null)
        {
            response.Headers.Add("A2UI-Conversation", conversationId);
        }

        return response;
    }
}

/// <summary>
/// A response body the test writes to while the client reads it.
/// </summary>
internal sealed class StreamingBody : Stream
{
    /// <summary>
    /// The chunks written and not yet read.
    /// </summary>
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();

    /// <summary>
    /// The chunk being read, and how far.
    /// </summary>
    private (byte[] Chunk, int Offset) _current = ([], 0);

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    /// <summary>
    /// Writes text for the client to read.
    /// </summary>
    /// <param name="text">The text.</param>
    public void Send(string text) => _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Ends the body.
    /// </summary>
    public void End() => _chunks.Writer.TryComplete();

    /// <summary>
    /// Breaks the body, as a dropped connection does.
    /// </summary>
    public void Break() => _chunks.Writer.TryComplete(new IOException("The connection was reset."));

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_current.Offset == _current.Chunk.Length)
        {
            try
            {
                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken))
                {
                    return 0;
                }
            }
            catch (ChannelClosedException ex) when (ex.InnerException is IOException io)
            {
                throw io;
            }

            if (_chunks.Reader.TryRead(out byte[]? chunk))
            {
                _current = (chunk, 0);
            }
        }

        int count = Math.Min(buffer.Length, _current.Chunk.Length - _current.Offset);
        _current.Chunk.AsSpan(_current.Offset, count).CopyTo(buffer.Span);
        _current = (_current.Chunk, _current.Offset + count);
        return count;
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
