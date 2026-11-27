using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace BlazorA2UI.BlazorServerSample;

/// <summary>
/// A pretend agent whose answers break, so the Errors page can show how the client reports a broken stream. It runs
/// inside the client's <see cref="HttpClient"/>, so no request leaves the app. It starts an answer and then either
/// stops in the middle of a message or fails as a dropped connection does.
/// </summary>
internal sealed class BrokenAgent : HttpMessageHandler
{
    /// <summary>
    /// The endpoint the Errors page gives the client. The handler answers every request itself, so nothing is sent
    /// there; the <c>.invalid</c> name could never resolve anyway.
    /// </summary>
    public static readonly Uri Endpoint = new("http://broken-agent.invalid/a2ui");

    /// <summary>
    /// The id of the surface the answer creates before it breaks.
    /// </summary>
    public const string SurfaceId = "broken";

    /// <summary>
    /// The break that ends the answer in the middle of a message.
    /// </summary>
    public const string Cut = "cut";

    /// <summary>
    /// The break that drops the connection.
    /// </summary>
    public const string Drop = "drop";

    /// <summary>
    /// The first, complete line of every answer, then the start of a message the answer never finishes.
    /// </summary>
    private static readonly byte[] s_answer = Encoding.UTF8.GetBytes(
        """{"version":"1.0","createSurface":{"surfaceId":"broken","catalogId":"https://a2ui.org/specification/v1_0/catalogs/basic/catalog.json","components":[{"id":"root","component":"Text","text":"The first part of the answer arrived. The rest never will."}]}}""" + "\n"
        + """{"version":"1.0","updateComponents":{"surfaceId":"broken","comp""");

    /// <summary>
    /// How the answer breaks: <see cref="Cut"/> or <see cref="Drop"/>.
    /// </summary>
    private readonly string _mode;

    /// <summary>
    /// Creates the pretend agent.
    /// </summary>
    /// <param name="mode"><see cref="Cut"/> or <see cref="Drop"/>.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="mode"/> is neither.</exception>
    public BrokenAgent(string mode)
    {
        if (mode is not (Cut or Drop))
        {
            throw new ArgumentException($"The mode must be '{Cut}' or '{Drop}'.", nameof(mode));
        }

        _mode = mode;
    }

    /// <summary>
    /// Answers a request: a complete message, half of the next, then the break.
    /// </summary>
    /// <returns>The answer.</returns>
    /// <param name="request">The request, which is not read.</param>
    /// <param name="cancellationToken">Not used, because the answer is ready at once.</param>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        StreamContent content = new(new BreakingStream(s_answer, _mode == Drop));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/jsonl");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request });
    }

    /// <summary>
    /// A read-only stream of fixed bytes that either ends after them or then fails as a dropped connection does.
    /// </summary>
    /// <param name="bytes">The bytes to return.</param>
    /// <param name="fail">Whether reading past the bytes throws instead of ending.</param>
    private sealed class BreakingStream(byte[] bytes, bool fail) : Stream
    {
        /// <summary>
        /// The bytes returned so far.
        /// </summary>
        private int _position;

        /// <summary>
        /// Gets true, because the stream can be read.
        /// </summary>
        public override bool CanRead => true;

        /// <summary>
        /// Gets false, because the stream cannot seek.
        /// </summary>
        public override bool CanSeek => false;

        /// <summary>
        /// Gets false, because the stream cannot be written.
        /// </summary>
        public override bool CanWrite => false;

        /// <summary>
        /// Not supported, because the stream cannot seek.
        /// </summary>
        public override long Length => throw new NotSupportedException();

        /// <summary>
        /// Not supported, because the stream cannot seek.
        /// </summary>
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <summary>
        /// Copies the next bytes, then ends or fails once they run out.
        /// </summary>
        /// <returns>The number of bytes copied, or 0 at the end.</returns>
        /// <param name="buffer">The buffer to fill.</param>
        /// <param name="offset">Where to start in <paramref name="buffer"/>.</param>
        /// <param name="count">The most bytes to copy.</param>
        /// <exception cref="IOException">Thrown past the bytes when the stream fails.</exception>
        public override int Read(byte[] buffer, int offset, int count)
        {
            int left = bytes.Length - _position;
            if (left == 0)
            {
                return fail ? throw new IOException("The connection was reset.") : 0;
            }

            int copied = Math.Min(left, count);
            Array.Copy(bytes, _position, buffer, offset, copied);
            _position += copied;
            return copied;
        }

        /// <summary>
        /// Does nothing, because the stream cannot be written.
        /// </summary>
        public override void Flush()
        {
        }

        /// <summary>
        /// Not supported, because the stream cannot seek.
        /// </summary>
        /// <returns>Never returns.</returns>
        /// <param name="offset">Not used.</param>
        /// <param name="origin">Not used.</param>
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <summary>
        /// Not supported, because the stream cannot seek.
        /// </summary>
        /// <param name="value">Not used.</param>
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <summary>
        /// Not supported, because the stream cannot be written.
        /// </summary>
        /// <param name="buffer">Not used.</param>
        /// <param name="offset">Not used.</param>
        /// <param name="count">Not used.</param>
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
