using System.Buffers;
using System.Text;

namespace BlazorA2UI.Client;

/// <summary>
/// Reads the lines of a response body as they arrive, with a bound on each line's length. A line ends at <c>\n</c>,
/// and a <c>\r</c> before it is dropped.
/// </summary>
internal sealed class A2UILineReader : IDisposable
{
    /// <summary>
    /// Decodes lines and refuses bytes that are not valid UTF-8.
    /// </summary>
    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The body.
    /// </summary>
    private readonly Stream _body;

    /// <summary>
    /// The longest line accepted, in bytes.
    /// </summary>
    private readonly int _maxLineLength;

    /// <summary>
    /// How long a read may wait for data, or <see cref="Timeout.InfiniteTimeSpan"/>.
    /// </summary>
    private readonly TimeSpan _readTimeout;

    /// <summary>
    /// The bytes read from the body and not yet returned.
    /// </summary>
    private byte[] _buffer;

    /// <summary>
    /// Where the unread bytes start in <see cref="_buffer"/>.
    /// </summary>
    private int _start;

    /// <summary>
    /// Where the unread bytes end in <see cref="_buffer"/>.
    /// </summary>
    private int _end;

    /// <summary>
    /// Whether the body has ended.
    /// </summary>
    private bool _ended;

    /// <summary>
    /// Creates a reader.
    /// </summary>
    /// <param name="body">The body.</param>
    /// <param name="maxLineLength">The longest line accepted, in bytes.</param>
    /// <param name="readTimeout">How long a read may wait for data, or <see cref="Timeout.InfiniteTimeSpan"/>.</param>
    public A2UILineReader(Stream body, int maxLineLength, TimeSpan readTimeout)
    {
        _body = body;
        _maxLineLength = maxLineLength;
        _readTimeout = readTimeout;
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Min(16 * 1024, maxLineLength + 2));
    }

    /// <summary>
    /// Reads the next line.
    /// </summary>
    /// <exception cref="A2UIAgentStreamException">
    /// A line is longer than the limit or is not valid UTF-8, the body ends in the middle of a line, or no data arrives
    /// within the read timeout.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <returns>The line without its line break, or null at the end of the body.</returns>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        int scanned = 0;
        while (true)
        {
            int newline = Array.IndexOf(_buffer, (byte)'\n', _start + scanned, _end - _start - scanned);
            if (newline >= 0)
            {
                int length = newline - _start;
                if (length > _maxLineLength + 1)
                {
                    throw TooLong();
                }

                string line = Decode(_buffer.AsSpan(_start, length));
                _start = newline + 1;
                return line;
            }

            scanned = _end - _start;

            // A '\r' may still come before the '\n', so the limit allows one byte more.
            if (scanned > _maxLineLength + 1)
            {
                throw TooLong();
            }

            if (_ended)
            {
                return scanned == 0
                    ? null
                    : throw new A2UIAgentStreamException("The agent's stream ended in the middle of a message.");
            }

            await FillAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns the buffer to the pool.
    /// </summary>
    public void Dispose()
    {
        byte[] buffer = _buffer;
        _buffer = [];
        if (buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Decodes a line, dropping a trailing <c>\r</c>.
    /// </summary>
    /// <exception cref="A2UIAgentStreamException">The line is too long or not valid UTF-8.</exception>
    /// <param name="bytes">The line's bytes.</param>
    private string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 0 && bytes[^1] == (byte)'\r')
        {
            bytes = bytes[..^1];
        }

        if (bytes.Length > _maxLineLength)
        {
            throw TooLong();
        }

        try
        {
            return s_utf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new A2UIAgentStreamException("The agent's stream is not valid UTF-8.");
        }
    }

    /// <summary>
    /// Reads more of the body, moving or growing the buffer first when it is full.
    /// </summary>
    /// <exception cref="A2UIAgentStreamException">No data arrived within the read timeout.</exception>
    /// <param name="cancellationToken">Cancels the read.</param>
    private async ValueTask FillAsync(CancellationToken cancellationToken)
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }

        if (_end == _buffer.Length)
        {
            byte[] larger = ArrayPool<byte>.Shared.Rent(Math.Min(_buffer.Length * 2, _maxLineLength + 2));
            Buffer.BlockCopy(_buffer, 0, larger, 0, _end);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = larger;
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_readTimeout);
        int read;
        try
        {
            read = await _body.ReadAsync(_buffer.AsMemory(_end), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new A2UIAgentStreamException($"The agent sent nothing for {_readTimeout.TotalSeconds:0.#} seconds.");
        }

        if (read == 0)
        {
            _ended = true;
        }

        _end += read;
    }

    /// <summary>
    /// Creates the error for a line over the limit.
    /// </summary>
    private A2UIAgentStreamException TooLong() =>
        new($"The agent sent a line longer than {_maxLineLength} bytes.");
}
