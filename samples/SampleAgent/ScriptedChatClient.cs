using System.Runtime.CompilerServices;

namespace BlazorA2UI.SampleAgent;

/// <summary>
/// A chat client that needs no model: it answers from a short script, in small chunks, as a model streams. Its answers
/// go through the same parser and checks as a real model's, so the sample runs offline and in CI.
/// </summary>
internal sealed class ScriptedChatClient : IChatClient
{
    /// <summary>
    /// How many characters each streamed chunk holds.
    /// </summary>
    internal const int ChunkLength = 48;

    /// <summary>
    /// The answer that shows the booking form.
    /// </summary>
    internal static readonly string s_bookingAnswer = "Let's find you a table.\n" + BookingSurfaces.ToBlock(BookingSurfaces.Form());

    /// <summary>
    /// The answer to anything the script does not know.
    /// </summary>
    internal static readonly string s_welcomeAnswer =
        "I'm the sample booking agent, running offline from a script. Ask me to book a table.\n" + BookingSurfaces.ToBlock(BookingSurfaces.Welcome());

    /// <summary>
    /// The answer to a request to repair messages that were not sent.
    /// </summary>
    internal const string RepairAnswer = "Sorry, something went wrong on my side. Ask me again to book a table.";

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Answer(messages))));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        string answer = Answer(messages);
        for (int start = 0; start < answer.Length; start += ChunkLength)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A pause between chunks, so the renderer visibly draws while the answer streams.
            await Task.Delay(10, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, answer.Substring(start, Math.Min(ChunkLength, answer.Length - start)));
        }
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <summary>
    /// Releases nothing; the client holds no resources.
    /// </summary>
    public void Dispose()
    {
    }

    /// <summary>
    /// Picks the script's answer to the latest user message.
    /// </summary>
    /// <returns>The answer.</returns>
    /// <param name="messages">The conversation so far.</param>
    internal static string Answer(IEnumerable<ChatMessage> messages)
    {
        string text = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        if (text.StartsWith(BookingAgent.RepairPrompt, StringComparison.Ordinal))
        {
            return RepairAnswer;
        }

        return text.Contains("book", StringComparison.OrdinalIgnoreCase) || text.Contains("table", StringComparison.OrdinalIgnoreCase)
            ? s_bookingAnswer
            : s_welcomeAnswer;
    }
}
