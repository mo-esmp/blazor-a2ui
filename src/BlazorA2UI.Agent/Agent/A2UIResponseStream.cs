using System.Text;
using BlazorA2UI.Evaluation;

namespace BlazorA2UI.Agent;

/// <summary>
/// Reads a model's answer as it streams, chunk by chunk, into text and A2UI messages.
/// </summary>
/// <remarks>
/// <para>
/// Text is returned as it arrives, except for the few characters at the end of a chunk that could start a tag or a
/// Markdown fence. Each message in a block is returned as soon as its JSON object closes, so the renderer can draw the
/// UI while the rest of the answer streams. Each chunk is read once.
/// </para>
/// <para>
/// It forgives the same mistakes as <see cref="A2UIResponseParser"/> and never throws for the model's text. A block or
/// message it cannot read becomes an <see cref="A2UIErrorPart"/>, and the rest of that block is skipped. The instance
/// is for one answer and is not safe for concurrent use.
/// </para>
/// </remarks>
public sealed class A2UIResponseStream
{
    /// <summary>
    /// The start of an open tag, without its closing <c>&gt;</c>.
    /// </summary>
    private const string OpenTagStart = "<a2ui-json";

    /// <summary>
    /// The start of a close tag, without its closing <c>&gt;</c>.
    /// </summary>
    private const string CloseTagStart = "</a2ui-json";

    /// <summary>
    /// The longest open tag with attributes that is held back while it may still be arriving.
    /// </summary>
    private const int MaxOpenTagLength = 256;

    /// <summary>
    /// The most characters of a chunk added to the open block before it is scanned.
    /// </summary>
    private const int SliceLength = 4096;

    /// <summary>
    /// The options.
    /// </summary>
    private readonly A2UIResponseParserOptions _options;

    /// <summary>
    /// Whether messages are inside tags; false when the whole input is the JSON of one block.
    /// </summary>
    private readonly bool _wrapped;

    /// <summary>
    /// Text outside blocks that has not been returned yet.
    /// </summary>
    private readonly StringBuilder _text = new();

    /// <summary>
    /// The unread part of the open block.
    /// </summary>
    private char[] _buffer = new char[256];

    /// <summary>
    /// How many characters of <see cref="_buffer"/> are in use.
    /// </summary>
    private int _length;

    /// <summary>
    /// Where the scan of the open block goes on, in <see cref="_buffer"/>.
    /// </summary>
    private int _position;

    /// <summary>
    /// Where the message being read starts, in <see cref="_buffer"/>.
    /// </summary>
    private int _itemStart;

    /// <summary>
    /// How many characters the open block has received.
    /// </summary>
    private int _blockLength;

    /// <summary>
    /// What the open block holds, once its first character has been seen.
    /// </summary>
    private BlockShape _shape;

    /// <summary>
    /// How deeply the scan is nested in the block's JSON.
    /// </summary>
    private int _depth;

    /// <summary>
    /// The bracket that opened each level of the block's JSON, from the outermost.
    /// </summary>
    private readonly char[] _openers = new char[JsonChecks.MaxDepth + 2];

    /// <summary>
    /// Whether the scan is inside a JSON string.
    /// </summary>
    private bool _inString;

    /// <summary>
    /// Whether the string the scan is inside opened with a curly quote.
    /// </summary>
    private bool _curlyString;

    /// <summary>
    /// Whether the block's array has just had a message, so a comma or its end must come next.
    /// </summary>
    private bool _needComma;

    /// <summary>
    /// The comment the scan is inside, if any.
    /// </summary>
    private CommentKind _comment;

    /// <summary>
    /// Whether the block's outermost JSON value has closed.
    /// </summary>
    private bool _closed;

    /// <summary>
    /// Whether the open block was rejected, so the rest of it is skipped.
    /// </summary>
    private bool _rejected;

    /// <summary>
    /// Whether a block is open.
    /// </summary>
    private bool _inBlock;

    /// <summary>
    /// Whether the text being read starts a segment: the answer, or the text after a block. Its leading whitespace
    /// and backticks are dropped.
    /// </summary>
    private bool _atSegmentStart = true;

    /// <summary>
    /// Whether the segment being read follows a block, so a Markdown fence that closes the block's fence may start it.
    /// </summary>
    private bool _afterBlock;

    /// <summary>
    /// How many messages have been returned.
    /// </summary>
    private int _messages;

    /// <summary>
    /// Whether <see cref="Complete"/> has run.
    /// </summary>
    private bool _completed;

    /// <summary>
    /// Creates a reader for one answer.
    /// </summary>
    /// <param name="options">The options, or null for the defaults.</param>
    public A2UIResponseStream(A2UIResponseParserOptions? options = null)
        : this(options, wrapped: true)
    {
    }

    /// <summary>
    /// Creates a reader for one answer, with or without tags around the JSON.
    /// </summary>
    /// <param name="options">The options, or null for the defaults.</param>
    /// <param name="wrapped">Whether messages are inside tags; false when the whole input is the JSON of one block.</param>
    internal A2UIResponseStream(A2UIResponseParserOptions? options, bool wrapped)
    {
        _options = options ?? new A2UIResponseParserOptions();
        _wrapped = wrapped;
        _inBlock = !wrapped;
    }

    /// <summary>
    /// What a block holds.
    /// </summary>
    private enum BlockShape
    {
        /// <summary>
        /// Nothing has been seen yet.
        /// </summary>
        Unknown,

        /// <summary>
        /// A JSON array of messages.
        /// </summary>
        Array,

        /// <summary>
        /// A single message.
        /// </summary>
        Object,
    }

    /// <summary>
    /// What follows a piece of text outside blocks.
    /// </summary>
    private enum TextEnd
    {
        /// <summary>
        /// More text of the same segment may follow.
        /// </summary>
        Open,

        /// <summary>
        /// A block follows.
        /// </summary>
        Block,

        /// <summary>
        /// The answer ends.
        /// </summary>
        Answer,
    }

    /// <summary>
    /// A comment in the block's JSON.
    /// </summary>
    private enum CommentKind
    {
        /// <summary>
        /// Not in a comment.
        /// </summary>
        None,

        /// <summary>
        /// A <c>//</c> comment, which ends at the line's end.
        /// </summary>
        Line,

        /// <summary>
        /// A <c>/*</c> comment, which ends at <c>*/</c>.
        /// </summary>
        Block,
    }

    /// <summary>
    /// The result of reading a character as part of a comment.
    /// </summary>
    private enum CommentStep
    {
        /// <summary>
        /// The character does not start or continue a comment.
        /// </summary>
        NotComment,

        /// <summary>
        /// The text ends before it can be told whether a comment starts or ends.
        /// </summary>
        Wait,

        /// <summary>
        /// The character, and maybe the one after it, belong to a comment and were skipped.
        /// </summary>
        Skipped,
    }

    /// <summary>
    /// The result of looking for a close tag at a position.
    /// </summary>
    private enum TagMatch
    {
        /// <summary>
        /// No close tag starts there.
        /// </summary>
        None,

        /// <summary>
        /// A close tag may start there, but the text ends before it can tell.
        /// </summary>
        Partial,

        /// <summary>
        /// A close tag starts there.
        /// </summary>
        Full,
    }

    /// <summary>
    /// Reads the next chunk of the answer.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="chunk"/> is null.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Complete"/> has already run.</exception>
    /// <returns>The parts the chunk completes, in order; empty when it completes none.</returns>
    /// <param name="chunk">The chunk.</param>
    public IReadOnlyList<A2UIResponsePart> Append(string chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (_completed)
        {
            throw new InvalidOperationException("The answer is complete; start a new stream for the next one.");
        }

        List<A2UIResponsePart> parts = [];
        ReadOnlyMemory<char> pending = chunk.AsMemory();
        while (pending.Length > 0)
        {
            if (_inBlock)
            {
                // Feed the block a slice at a time, never past its limit, so a large chunk cannot grow the buffer past
                // the limit and text after the close tag is not counted as part of the block.
                int allowance = _rejected ? SliceLength : _options.MaxBlockLength - _blockLength;
                if (allowance <= 0)
                {
                    Reject(parts, $"The A2UI JSON block is longer than {_options.MaxBlockLength} characters.");
                    continue;
                }

                int take = Math.Min(pending.Length, Math.Min(SliceLength, allowance));
                AppendToBlock(pending.Span[..take]);
                ReadOnlyMemory<char> rest = pending[take..];
                pending = ReadOnlyMemory<char>.Empty;
                int end = ScanBlock(parts);
                if (end < 0)
                {
                    pending = rest;
                    continue;
                }

                pending = string.Concat(_buffer.AsSpan(end, _length - end), rest.Span).AsMemory();
                EndBlock();
                continue;
            }

            _text.Append(pending.Span);
            pending = ReadOnlyMemory<char>.Empty;
            string text = _text.ToString();
            int open = text.IndexOf(OpenTagStart, StringComparison.OrdinalIgnoreCase);
            while (open >= 0)
            {
                int close = OpenTagEnd(text, open);
                if (close == -1)
                {
                    // It may still be an open tag; wait for the rest.
                    break;
                }

                if (close > 0)
                {
                    EmitText(text[..open], parts, TextEnd.Block);
                    _text.Clear();
                    _inBlock = true;
                    _atSegmentStart = true;
                    pending = text.AsMemory(close);
                    break;
                }

                open = text.IndexOf(OpenTagStart, open + 1, StringComparison.OrdinalIgnoreCase);
            }

            if (_inBlock)
            {
                continue;
            }

            int cut = MayStartClosingFence(text) ? 0 : SafeTextEnd(text);
            EmitText(text[..cut], parts, TextEnd.Open);
            _text.Remove(0, cut);
        }

        return parts;
    }

    /// <summary>
    /// Ends the answer and returns what was held back: the last text, and an error for a block that never closed.
    /// Calling it again returns nothing.
    /// </summary>
    /// <returns>The remaining parts, in order.</returns>
    public IReadOnlyList<A2UIResponsePart> Complete()
    {
        List<A2UIResponsePart> parts = [];
        if (_completed)
        {
            return parts;
        }

        _completed = true;
        if (_inBlock && !_rejected)
        {
            if (_shape == BlockShape.Unknown)
            {
                parts.Add(new A2UIErrorPart(_wrapped ? "The answer ended inside an A2UI JSON block with nothing in it." : "The JSON is empty."));
            }
            else if (!_closed)
            {
                parts.Add(new A2UIErrorPart(_wrapped
                    ? $"The answer ended inside an A2UI JSON block before its JSON was complete. Close every block with {A2UIResponseParser.CloseTag}."
                    : "The JSON ended before it was complete."));
            }
            else if (_comment != CommentKind.None || (_position < _length && _buffer[_position] == '/'))
            {
                parts.Add(new A2UIErrorPart(_wrapped
                    ? "The answer ended inside an unfinished comment in an A2UI JSON block."
                    : "The JSON ends with an unfinished comment."));
            }
        }

        if (_text.Length > 0)
        {
            EmitText(_text.ToString(), parts, TextEnd.Answer);
            _text.Clear();
        }

        return parts;
    }

    /// <summary>
    /// Returns where an open tag that starts at a position ends.
    /// </summary>
    /// <returns>The index after its <c>&gt;</c>; -1 when the text ends before that can be told; 0 when no open tag starts there.</returns>
    /// <param name="text">The text.</param>
    /// <param name="start">Where <see cref="OpenTagStart"/> was found.</param>
    private static int OpenTagEnd(string text, int start)
    {
        int i = start + OpenTagStart.Length;
        if (i >= text.Length)
        {
            return -1;
        }

        if (text[i] == '>')
        {
            return i + 1;
        }

        if (!char.IsWhiteSpace(text[i]))
        {
            return 0;
        }

        // The whole tag, from its < to its >, may be at most MaxOpenTagLength characters, however the text is split.
        int limit = Math.Min(text.Length, start + MaxOpenTagLength);
        int end = text.IndexOf('>', i, limit - i);
        if (end >= 0)
        {
            return end + 1;
        }

        return limit - start >= MaxOpenTagLength ? 0 : -1;
    }

    /// <summary>
    /// Returns how much of the text outside blocks can be returned now: all of it but a tail that could start an open
    /// tag, or a Markdown fence, whole or partly written, and whitespace that would be dropped if a tag follows.
    /// </summary>
    /// <param name="text">The text.</param>
    private static int SafeTextEnd(string text)
    {
        int cut = text.Length;
        for (int i = Math.Max(0, text.Length - MaxOpenTagLength); i < text.Length; i++)
        {
            if (text[i] != '<')
            {
                continue;
            }

            int rest = text.Length - i;
            bool prefix = rest < OpenTagStart.Length
                && OpenTagStart.AsSpan(0, rest).Equals(text.AsSpan(i), StringComparison.OrdinalIgnoreCase);
            if (prefix || (rest >= OpenTagStart.Length && OpenTagEnd(text, i) == -1
                && text.AsSpan(i, OpenTagStart.Length).Equals(OpenTagStart, StringComparison.OrdinalIgnoreCase)))
            {
                cut = i;
                break;
            }
        }

        int fence = TrailingFenceStart(text, cut);

        // A line of one or two backticks may still become a fence.
        int ticks = cut;
        while (ticks > 0 && text[ticks - 1] == '`')
        {
            ticks--;
        }

        if (cut - ticks is 1 or 2)
        {
            int lineStart = ticks;
            while (lineStart > 0 && text[lineStart - 1] is ' ' or '\t')
            {
                lineStart--;
            }

            if (lineStart == 0 || text[lineStart - 1] is '\n' or '\r')
            {
                return Math.Min(fence, TrailingWhitespaceStart(text, ticks));
            }
        }

        return fence;
    }

    /// <summary>
    /// Returns where a run of whitespace, or a Markdown fence line such as <c>```json</c> with the whitespace around
    /// it, that ends at a position begins. A fence counts only when its backticks start a line.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="end">Where the run ends.</param>
    private static int TrailingFenceStart(string text, int end)
    {
        int i = TrailingWhitespaceStart(text, end);
        int word = i;
        while (word > 0 && (char.IsAsciiLetterOrDigit(text[word - 1]) || text[word - 1] is '-' or '+' or '_'))
        {
            word--;
        }

        int ticks = word;
        while (ticks > 0 && text[ticks - 1] == '`')
        {
            ticks--;
        }

        if (word - ticks != 3)
        {
            return i;
        }

        int lineStart = ticks;
        while (lineStart > 0 && text[lineStart - 1] is ' ' or '\t')
        {
            lineStart--;
        }

        return lineStart == 0 || text[lineStart - 1] is '\n' or '\r' ? TrailingWhitespaceStart(text, ticks) : i;
    }

    /// <summary>
    /// Returns where a run of whitespace that ends at a position begins.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="end">Where the run ends.</param>
    private static int TrailingWhitespaceStart(string text, int end)
    {
        int i = end;
        while (i > 0 && char.IsWhiteSpace(text[i - 1]))
        {
            i--;
        }

        return i;
    }

    /// <summary>
    /// Returns whether the text right after a block may still turn into the Markdown fence that closes the block's
    /// fence, so it must wait for more text: whitespace and then one or two backticks.
    /// </summary>
    /// <param name="text">The text outside blocks that has not been returned yet.</param>
    private bool MayStartClosingFence(string text)
    {
        if (!_atSegmentStart || !_afterBlock)
        {
            return false;
        }

        string rest = text.TrimStart();
        return rest.Length is > 0 and < 3 && rest.All(c => c == '`');
    }

    /// <summary>
    /// Returns text outside blocks as a part. At the start of a segment, leading whitespace is dropped, and right after
    /// a block so is the Markdown fence line that closes the block's fence. Before a block, the fence line that opens
    /// the block's fence and the whitespace are dropped; at the end of the answer, only the whitespace is. Backticks
    /// anywhere else are the answer's own Markdown and are kept.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="parts">Receives the part.</param>
    /// <param name="textEnd">What follows the text.</param>
    private void EmitText(string text, List<A2UIResponsePart> parts, TextEnd textEnd)
    {
        int start = 0;
        if (_atSegmentStart)
        {
            start = text.Length - text.AsSpan().TrimStart().Length;
            if (_afterBlock && text.AsSpan(start).StartsWith("```", StringComparison.Ordinal)
                && (start + 3 == text.Length || char.IsWhiteSpace(text[start + 3])))
            {
                start += 3;
                start = text.Length - text.AsSpan(start).TrimStart().Length;
            }
        }

        int end = textEnd switch
        {
            TextEnd.Block => TrailingFenceStart(text, text.Length),
            TextEnd.Answer => TrailingWhitespaceStart(text, text.Length),
            _ => text.Length,
        };
        if (end > start)
        {
            _atSegmentStart = false;
            parts.Add(new A2UITextPart(text[start..end]));
        }
    }

    /// <summary>
    /// Adds text to the open block.
    /// </summary>
    /// <param name="text">The text.</param>
    private void AppendToBlock(ReadOnlySpan<char> text)
    {
        _blockLength += text.Length;
        if (_length + text.Length > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + text.Length));
        }

        text.CopyTo(_buffer.AsSpan(_length));
        _length += text.Length;
    }

    /// <summary>
    /// Reads the open block as far as it has arrived, returning each message whose JSON closes.
    /// </summary>
    /// <returns>Where the text after the block's close tag starts in <see cref="_buffer"/>, or -1 while the block is open.</returns>
    /// <param name="parts">Receives messages and errors.</param>
    private int ScanBlock(List<A2UIResponsePart> parts)
    {
        while (_position < _length)
        {
            char c = _buffer[_position];
            if (_inString && !_rejected)
            {
                if (c == '\\')
                {
                    if (_position + 1 >= _length)
                    {
                        break;
                    }

                    _position += 2;
                    continue;
                }

                _inString = _curlyString ? c is not ('"' or '\u201C' or '\u201D') : c != '"';
                _position++;
                continue;
            }

            // A close tag ends the block outside strings and /* */ comments. A // comment ends at a close tag, since
            // a model may put one on the same line as its last comment.
            if (c == '<' && _wrapped && _comment != CommentKind.Block)
            {
                TagMatch match = CloseTagAt(_position, out int end);
                if (match == TagMatch.Partial)
                {
                    break;
                }

                if (match == TagMatch.Full)
                {
                    FinishBlock(parts);
                    return end;
                }
            }

            if (_rejected)
            {
                _position++;
                continue;
            }

            if (_comment != CommentKind.None || c == '/')
            {
                CommentStep step = StepComment(c);
                if (step == CommentStep.Wait)
                {
                    break;
                }

                if (step == CommentStep.Skipped)
                {
                    continue;
                }
            }

            if (_shape == BlockShape.Unknown)
            {
                if (!StartPayload(c, parts))
                {
                    break;
                }

                continue;
            }

            if (_closed)
            {
                if (!char.IsWhiteSpace(c) && c != '`')
                {
                    Reject(parts, "The A2UI JSON block has text after its JSON. Put only a JSON array of messages in a block.");
                    continue;
                }

                _position++;
                continue;
            }

            ScanJson(c, parts);
        }

        Compact();
        return -1;
    }

    /// <summary>
    /// Skips a character that starts, continues or ends a <c>//</c> or <c>/*</c> comment outside strings, as the JSON
    /// reader allows comments.
    /// </summary>
    /// <returns>Whether the character was skipped, or the scan must wait for more text.</returns>
    /// <param name="c">The character at the scan position.</param>
    private CommentStep StepComment(char c)
    {
        bool hasNext = _position + 1 < _length;
        char next = hasNext ? _buffer[_position + 1] : '\0';
        switch (_comment)
        {
            case CommentKind.Line:
                _comment = c == '\n' ? CommentKind.None : CommentKind.Line;
                _position++;
                return CommentStep.Skipped;
            case CommentKind.Block when c == '*':
                if (!hasNext)
                {
                    return CommentStep.Wait;
                }

                _comment = next == '/' ? CommentKind.None : CommentKind.Block;
                _position += next == '/' ? 2 : 1;
                return CommentStep.Skipped;
            case CommentKind.Block:
                _position++;
                return CommentStep.Skipped;
            default:
                if (!hasNext)
                {
                    return CommentStep.Wait;
                }

                if (next is not ('/' or '*'))
                {
                    return CommentStep.NotComment;
                }

                _comment = next == '/' ? CommentKind.Line : CommentKind.Block;
                _position += 2;
                return CommentStep.Skipped;
        }
    }

    /// <summary>
    /// Reads the first characters of a block: whitespace, a Markdown fence line, then the <c>[</c> or <c>{</c> that
    /// starts its JSON.
    /// </summary>
    /// <returns>False when the scan must wait for more text.</returns>
    /// <param name="c">The character at the scan position.</param>
    /// <param name="parts">Receives an error when the block does not start with JSON.</param>
    private bool StartPayload(char c, List<A2UIResponsePart> parts)
    {
        if (char.IsWhiteSpace(c))
        {
            _position++;
            return true;
        }

        if (c == '`')
        {
            int newline = Array.IndexOf(_buffer, '\n', _position, _length - _position);
            if (newline < 0)
            {
                return false;
            }

            _position = newline + 1;
            return true;
        }

        if (c == '[')
        {
            _shape = BlockShape.Array;
            _openers[0] = c;
            _depth = 1;
            _position++;
            return true;
        }

        if (c == '{')
        {
            _shape = BlockShape.Object;
            _itemStart = _position;
            _openers[0] = c;
            _depth = 1;
            _position++;
            return true;
        }

        Reject(parts, "An A2UI JSON block must hold a JSON array of messages.");
        return true;
    }

    /// <summary>
    /// Reads one character of the block's JSON outside strings, returning a message when its object closes.
    /// </summary>
    /// <param name="c">The character.</param>
    /// <param name="parts">Receives messages and errors.</param>
    private void ScanJson(char c, List<A2UIResponsePart> parts)
    {
        bool betweenItems = _shape == BlockShape.Array && _depth == 1;
        switch (c)
        {
            case '"' or '\u201C' or '\u201D':
                if (betweenItems)
                {
                    Reject(parts, "Each item of an A2UI JSON block must be a message object.");
                    return;
                }

                // A string opened with a curly quote may close with any double quote, as LenientJson straightens them.
                _inString = true;
                _curlyString = c != '"';
                break;
            case '{' or '[':
                if (betweenItems)
                {
                    if (c != '{')
                    {
                        Reject(parts, "Each item of an A2UI JSON block must be a message object.");
                        return;
                    }

                    if (_needComma)
                    {
                        Reject(parts, "The messages in an A2UI JSON block must be separated by commas.");
                        return;
                    }

                    _itemStart = _position;
                }

                _depth++;
                if (_depth > JsonChecks.MaxDepth + 2)
                {
                    Reject(parts, $"The JSON in the A2UI JSON block is nested more than {JsonChecks.MaxDepth} levels deep.");
                    return;
                }

                _openers[_depth - 1] = c;
                break;
            case '}' or ']':
                if (c != (_openers[_depth - 1] == '[' ? ']' : '}'))
                {
                    Reject(parts, $"The JSON in the A2UI JSON block has a '{c}' that does not match the bracket it closes.");
                    return;
                }

                _depth--;
                if (_depth == 1 && _shape == BlockShape.Array && c == '}')
                {
                    if (!EmitMessage(_itemStart, _position + 1, parts))
                    {
                        return;
                    }

                    _needComma = true;
                }
                else if (_depth == 0)
                {
                    if (_shape == BlockShape.Object && !EmitMessage(_itemStart, _position + 1, parts))
                    {
                        return;
                    }

                    _closed = true;
                }

                break;
            case ',' when betweenItems:
                // One comma after each message; a trailing comma before ] is forgiven.
                if (!_needComma)
                {
                    Reject(parts, "An A2UI JSON block has a comma where a message should be.");
                    return;
                }

                _needComma = false;
                break;
            default:
                if (betweenItems && !char.IsWhiteSpace(c))
                {
                    Reject(parts, "Each item of an A2UI JSON block must be a message object.");
                    return;
                }

                break;
        }

        _position++;
    }

    /// <summary>
    /// Reads one message from the block and returns it, or rejects the block.
    /// </summary>
    /// <returns>False when the block was rejected.</returns>
    /// <param name="start">Where the message's JSON starts in <see cref="_buffer"/>.</param>
    /// <param name="end">Where it ends.</param>
    /// <param name="parts">Receives the message or an error.</param>
    private bool EmitMessage(int start, int end, List<A2UIResponsePart> parts)
    {
        int number = _messages + 1;
        if (_messages >= _options.MaxMessages)
        {
            Reject(parts, $"The answer holds more than {_options.MaxMessages} A2UI messages.");
            return false;
        }

        if (!LenientJson.TryParse(new string(_buffer, start, end - start), out JsonNode? node, out string? error))
        {
            Reject(parts, $"A2UI message {number} is not valid JSON: {error}");
            return false;
        }

        if (CheckEnvelope(node, _options.AllowedMessages) is not { } message)
        {
            Reject(parts, $"A2UI message {number} {EnvelopeProblem(node, _options.AllowedMessages)}");
            return false;
        }

        _messages++;
        parts.Add(new A2UIMessagePart(message));
        return true;
    }

    /// <summary>
    /// Returns a message with a valid envelope, adding a missing <c>version</c>, or null when the envelope is wrong.
    /// </summary>
    /// <param name="node">The message.</param>
    /// <param name="allowed">The messages allowed.</param>
    private static JsonObject? CheckEnvelope(JsonNode? node, A2UIAgentMessages allowed) =>
        EnvelopeProblem(node, allowed) is null ? WithVersion((JsonObject)node!) : null;

    /// <summary>
    /// Returns what is wrong with a message's envelope, or null when nothing is.
    /// </summary>
    /// <param name="node">The message.</param>
    /// <param name="allowed">The messages allowed.</param>
    private static string? EnvelopeProblem(JsonNode? node, A2UIAgentMessages allowed)
    {
        if (node is not JsonObject message)
        {
            return "is not a JSON object.";
        }

        if (message.ContainsKey("version") && A2UIValues.AsString(message["version"]) != A2UIProtocol.Version)
        {
            return $"must have \"version\": \"{A2UIProtocol.Version}\".";
        }

        string? key = null;
        foreach ((string name, JsonNode? _) in message)
        {
            if (name == "version")
            {
                continue;
            }

            if (key is not null)
            {
                return "must hold exactly one message, such as 'createSurface' or 'updateComponents'.";
            }

            key = name;
        }

        if (key is null)
        {
            return "must hold a message, such as 'createSurface' or 'updateComponents'.";
        }

        A2UIAgentMessages kind = AgentMessageNames.FromKey(key);
        if (kind == A2UIAgentMessages.None)
        {
            return $"has the unknown message '{key}'.";
        }

        return (kind & allowed) == 0 ? $"is a '{key}' message, which is not allowed here." : null;
    }

    /// <summary>
    /// Returns the message with <c>version</c> first, adding it when it is missing.
    /// </summary>
    /// <param name="message">The message.</param>
    private static JsonObject WithVersion(JsonObject message)
    {
        if (message.ContainsKey("version"))
        {
            return message;
        }

        JsonObject result = new() { ["version"] = A2UIProtocol.Version };
        foreach ((string key, JsonNode? value) in message.ToList())
        {
            message.Remove(key);
            result[key] = value;
        }

        return result;
    }

    /// <summary>
    /// Looks for a close tag at a position of the block.
    /// </summary>
    /// <returns>Whether a close tag starts there.</returns>
    /// <param name="start">The position of a <c>&lt;</c>.</param>
    /// <param name="end">Set to the index after the tag when it is complete.</param>
    private TagMatch CloseTagAt(int start, out int end)
    {
        end = 0;
        int available = _length - start;
        int compare = Math.Min(available, CloseTagStart.Length);
        if (!CloseTagStart.AsSpan(0, compare).Equals(_buffer.AsSpan(start, compare), StringComparison.OrdinalIgnoreCase))
        {
            return TagMatch.None;
        }

        int i = start + CloseTagStart.Length;
        while (i < _length && char.IsWhiteSpace(_buffer[i]) && i - start < MaxOpenTagLength)
        {
            i++;
        }

        if (i >= _length)
        {
            return TagMatch.Partial;
        }

        if (_buffer[i] != '>')
        {
            return TagMatch.None;
        }

        end = i + 1;
        return TagMatch.Full;
    }

    /// <summary>
    /// Reports a block whose close tag arrived before its JSON was complete.
    /// </summary>
    /// <param name="parts">Receives the error.</param>
    private void FinishBlock(List<A2UIResponsePart> parts)
    {
        if (_rejected)
        {
            return;
        }

        if (_shape == BlockShape.Unknown)
        {
            parts.Add(new A2UIErrorPart("An A2UI JSON block is empty. Put a JSON array of messages in it."));
        }
        else if (!_closed)
        {
            parts.Add(new A2UIErrorPart("An A2UI JSON block closed before its JSON was complete."));
        }
    }

    /// <summary>
    /// Rejects the open block: reports the error and skips the rest of the block.
    /// </summary>
    /// <param name="parts">Receives the error.</param>
    /// <param name="error">What went wrong.</param>
    private void Reject(List<A2UIResponsePart> parts, string error)
    {
        parts.Add(new A2UIErrorPart(error));
        _rejected = true;
        _inString = false;
        _comment = CommentKind.None;
    }

    /// <summary>
    /// Forgets the closed block and gets ready for text.
    /// </summary>
    private void EndBlock()
    {
        _inBlock = !_wrapped;
        _length = 0;
        _position = 0;
        _itemStart = 0;
        _blockLength = 0;
        _shape = BlockShape.Unknown;
        _depth = 0;
        _inString = false;
        _curlyString = false;
        _needComma = false;
        _comment = CommentKind.None;
        _closed = false;
        _rejected = false;
        _atSegmentStart = true;
        _afterBlock = true;
    }

    /// <summary>
    /// Drops the part of the block that has been read and is no longer needed, so a long block does not keep its
    /// returned messages in memory.
    /// </summary>
    private void Compact()
    {
        bool inMessage = !_rejected && _shape != BlockShape.Unknown && !_closed && (_shape == BlockShape.Object || _depth >= 2);
        int keep = inMessage ? _itemStart : _position;
        if (keep == 0)
        {
            return;
        }

        Array.Copy(_buffer, keep, _buffer, 0, _length - keep);
        _length -= keep;
        _position -= keep;
        _itemStart -= keep;
    }
}
