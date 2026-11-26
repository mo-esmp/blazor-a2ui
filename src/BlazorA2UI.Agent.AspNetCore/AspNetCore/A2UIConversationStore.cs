namespace BlazorA2UI.Agent.AspNetCore;

/// <summary>
/// The conversations of one endpoint, bounded in number and expired when idle.
/// </summary>
internal sealed class A2UIConversationStore : IDisposable
{
    /// <summary>
    /// The length of an id: 16 random bytes in base64url without padding.
    /// </summary>
    internal const int IdLength = 22;

    /// <summary>
    /// The conversations by id.
    /// </summary>
    private readonly Dictionary<string, A2UIConversation> _conversations = new(StringComparer.Ordinal);

    /// <summary>
    /// Guards <see cref="_conversations"/> and <see cref="_disposed"/>.
    /// </summary>
    private readonly Lock _gate = new();

    /// <summary>
    /// The endpoint's options.
    /// </summary>
    private readonly A2UIAgentEndpointOptions _options;

    /// <summary>
    /// Removes idle conversations regularly.
    /// </summary>
    private readonly ITimer _sweeper;

    /// <summary>
    /// Whether the store was disposed.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Creates a store.
    /// </summary>
    /// <param name="options">The endpoint's options.</param>
    public A2UIConversationStore(A2UIAgentEndpointOptions options)
    {
        _options = options;
        TimeSpan period = options.ConversationIdleTimeout / 2;
        _sweeper = options.TimeProvider.CreateTimer(_ => Sweep(), null, period, period);
    }

    /// <summary>
    /// Gets how many conversations the store holds.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _conversations.Count;
            }
        }
    }

    /// <summary>
    /// Starts a conversation and acquires it for the calling request.
    /// </summary>
    /// <returns>The conversation, or null when the store is full or disposed.</returns>
    public A2UIConversation? Create()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            if (_conversations.Count >= _options.MaxConversations)
            {
                SweepLocked();
                if (_conversations.Count >= _options.MaxConversations)
                {
                    return null;
                }
            }

            string id = NewId();
            A2UIConversation conversation = new(id, _options);
            conversation.TryAcquire();
            _conversations.Add(id, conversation);
            return conversation;
        }
    }

    /// <summary>
    /// Finds a conversation and acquires it for the calling request.
    /// </summary>
    /// <returns>The conversation, or null when the id is malformed, unknown or expired.</returns>
    /// <param name="id">The id the client sent.</param>
    public A2UIConversation? Acquire(string id)
    {
        if (!IsWellFormedId(id))
        {
            return null;
        }

        lock (_gate)
        {
            if (!_conversations.TryGetValue(id, out A2UIConversation? conversation))
            {
                return null;
            }

            if (conversation.TryExpire(_options.TimeProvider.GetUtcNow(), _options.ConversationIdleTimeout))
            {
                _conversations.Remove(id);
                return null;
            }

            return conversation.TryAcquire() ? conversation : null;
        }
    }

    /// <summary>
    /// Marks a request as done with a conversation.
    /// </summary>
    /// <param name="conversation">The conversation.</param>
    public void Release(A2UIConversation conversation) => conversation.Release(_options.TimeProvider.GetUtcNow());

    /// <summary>
    /// Removes the conversations that have been idle for longer than the timeout.
    /// </summary>
    public void Sweep()
    {
        lock (_gate)
        {
            SweepLocked();
        }
    }

    /// <summary>
    /// Disposes every conversation and stops expiring them.
    /// </summary>
    public void Dispose()
    {
        List<A2UIConversation> conversations;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            conversations = [.. _conversations.Values];
            _conversations.Clear();
        }

        _sweeper.Dispose();
        foreach (A2UIConversation conversation in conversations)
        {
            conversation.Dispose();
        }
    }

    /// <summary>
    /// Returns whether an id has the shape of one the store issues, so malformed ids are refused without a lookup.
    /// </summary>
    /// <param name="id">The id.</param>
    internal static bool IsWellFormedId(string? id) =>
        id is { Length: IdLength } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>
    /// Returns a new random id: 128 bits from a cryptographic generator, so ids cannot be guessed.
    /// </summary>
    private static string NewId()
    {
        Span<byte> bytes = stackalloc byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return System.Buffers.Text.Base64Url.EncodeToString(bytes);
    }

    /// <summary>
    /// Removes the idle conversations. The caller holds <see cref="_gate"/>.
    /// </summary>
    private void SweepLocked()
    {
        DateTimeOffset now = _options.TimeProvider.GetUtcNow();
        List<string> expired = [];
        foreach ((string id, A2UIConversation conversation) in _conversations)
        {
            if (conversation.TryExpire(now, _options.ConversationIdleTimeout))
            {
                expired.Add(id);
            }
        }

        foreach (string id in expired)
        {
            _conversations.Remove(id);
        }
    }
}
