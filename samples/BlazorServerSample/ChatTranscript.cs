namespace BlazorA2UI.BlazorServerSample;

/// <summary>
/// The chat's transcript: what the user said and the agent's text, kept for as long as the conversation. It is scoped
/// like the agent client, so leaving the Chat page and coming back shows the conversation as it was, and the agent's
/// text that arrives while another page is open is kept too. Both samples use it: the WebAssembly sample links this file.
/// </summary>
internal sealed class ChatTranscript : IDisposable
{
    /// <summary>
    /// The most entries the transcript keeps.
    /// </summary>
    public const int MaxEntries = 50;

    /// <summary>
    /// The longest text kept for one of the agent's entries.
    /// </summary>
    public const int MaxEntryLength = 10_000;

    /// <summary>
    /// The client whose turns the transcript follows.
    /// </summary>
    private readonly A2UIAgentClient _agent;

    /// <summary>
    /// The entries, oldest first.
    /// </summary>
    private readonly List<ChatEntry> _entries = [];

    /// <summary>
    /// The entry the agent's text goes into during the current turn, or null before it sends any.
    /// </summary>
    private ChatEntry? _answer;

    /// <summary>
    /// Creates the transcript and starts following the client's turns.
    /// </summary>
    /// <param name="agent">The scope's agent client.</param>
    public ChatTranscript(A2UIAgentClient agent)
    {
        _agent = agent;
        _agent.StateChanged += OnStateChanged;
        _agent.TextReceived += OnTextReceived;
    }

    /// <summary>
    /// Raised when an entry is added or the agent's text grows. It runs on the client's synchronization context.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Gets the entries, oldest first.
    /// </summary>
    public IReadOnlyList<ChatEntry> Entries => _entries;

    /// <summary>
    /// Adds what the user said.
    /// </summary>
    /// <param name="text">The user's text, already checked against the input limit.</param>
    public void AddUser(string text)
    {
        Add(new ChatEntry("user", text));
        Changed?.Invoke();
    }

    /// <summary>
    /// Stops following the client, when the scope ends.
    /// </summary>
    public void Dispose()
    {
        _agent.StateChanged -= OnStateChanged;
        _agent.TextReceived -= OnTextReceived;
    }

    /// <summary>
    /// Adds the agent's text to its entry for this turn, up to the length limit. The text comes from the model, so the
    /// page shows it as text.
    /// </summary>
    /// <param name="text">A piece of the agent's text.</param>
    private void OnTextReceived(string text)
    {
        if (_answer is null)
        {
            _answer = new ChatEntry("agent", string.Empty);
            Add(_answer);
        }

        int room = MaxEntryLength - _answer.Text.Length;
        if (room > 0)
        {
            _answer.Text += text.Length <= room ? text : text[..room];
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Starts a new agent entry when a turn starts, whether typed text or a surface's action started it.
    /// </summary>
    private void OnStateChanged()
    {
        if (_agent.State is A2UIConnectionState.Connecting)
        {
            _answer = null;
        }
    }

    /// <summary>
    /// Adds an entry, dropping the oldest past the limit.
    /// </summary>
    /// <param name="entry">The entry.</param>
    private void Add(ChatEntry entry)
    {
        _entries.Add(entry);
        if (_entries.Count > MaxEntries)
        {
            _entries.RemoveAt(0);
        }
    }
}

/// <summary>
/// One line of the transcript.
/// </summary>
/// <param name="kind">Who wrote it: <c>user</c> or <c>agent</c>, also its CSS class.</param>
/// <param name="text">The text.</param>
internal sealed class ChatEntry(string kind, string text)
{
    /// <summary>
    /// Gets who wrote the line.
    /// </summary>
    public string Kind { get; } = kind;

    /// <summary>
    /// Gets or sets the text, which grows as the agent's answer streams.
    /// </summary>
    public string Text { get; set; } = text;
}
