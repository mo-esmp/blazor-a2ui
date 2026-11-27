namespace BlazorA2UI.SampleAgent;

/// <summary>
/// Keeps each conversation's recent chat messages, so the model sees what it said before. It is bounded twice: each
/// conversation keeps its latest messages, and when too many conversations are kept the one used longest ago is
/// dropped.
/// </summary>
/// <param name="maxConversations">The most conversations kept.</param>
/// <param name="maxMessages">The most messages kept for one conversation.</param>
internal sealed class ConversationHistory(int maxConversations = 1000, int maxMessages = 20)
{
    /// <summary>
    /// Guards <see cref="_conversations"/> and <see cref="_recent"/>.
    /// </summary>
    private readonly Lock _gate = new();

    /// <summary>
    /// The messages of each conversation, with its place in <see cref="_recent"/>.
    /// </summary>
    private readonly Dictionary<string, (List<ChatMessage> Messages, LinkedListNode<string> Node)> _conversations = new(StringComparer.Ordinal);

    /// <summary>
    /// The conversations' ids, used longest ago first.
    /// </summary>
    private readonly LinkedList<string> _recent = [];

    /// <summary>
    /// Gets how many conversations are kept.
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
    /// Returns a copy of a conversation's messages, oldest first; empty for a conversation not kept.
    /// </summary>
    /// <returns>The messages.</returns>
    /// <param name="conversationId">The conversation's id.</param>
    public List<ChatMessage> Get(string conversationId)
    {
        lock (_gate)
        {
            if (!_conversations.TryGetValue(conversationId, out (List<ChatMessage> Messages, LinkedListNode<string> Node) entry))
            {
                return [];
            }

            Touch(entry.Node);
            return [.. entry.Messages];
        }
    }

    /// <summary>
    /// Adds messages to a conversation, dropping its oldest past the limit, and drops the conversation used longest
    /// ago when there are too many.
    /// </summary>
    /// <param name="conversationId">The conversation's id.</param>
    /// <param name="messages">The messages, oldest first.</param>
    public void Add(string conversationId, params ReadOnlySpan<ChatMessage> messages)
    {
        lock (_gate)
        {
            if (!_conversations.TryGetValue(conversationId, out (List<ChatMessage> Messages, LinkedListNode<string> Node) entry))
            {
                entry = ([], _recent.AddLast(conversationId));
                _conversations[conversationId] = entry;
                if (_conversations.Count > maxConversations && _recent.First is { } oldest)
                {
                    _conversations.Remove(oldest.Value);
                    _recent.RemoveFirst();
                }
            }
            else
            {
                Touch(entry.Node);
            }

            entry.Messages.AddRange(messages);
            if (entry.Messages.Count > maxMessages)
            {
                entry.Messages.RemoveRange(0, entry.Messages.Count - maxMessages);
            }
        }
    }

    /// <summary>
    /// Marks a conversation as just used.
    /// </summary>
    /// <param name="node">The conversation's place in <see cref="_recent"/>.</param>
    private void Touch(LinkedListNode<string> node)
    {
        _recent.Remove(node);
        _recent.AddLast(node);
    }
}
