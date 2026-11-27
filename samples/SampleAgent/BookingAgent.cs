using System.Runtime.CompilerServices;
using System.Text;

namespace BlazorA2UI.SampleAgent;

/// <summary>
/// Runs the sample's turns. The <c>book</c> action is handled in code, end to end: the agent checks the booking, asks
/// the renderer for the user's time zone and replaces the form with a confirmation. Text, other actions and the
/// renderer's errors go to the model, whose answer streams to the renderer as it arrives.
/// </summary>
/// <param name="chat">The model, or the offline script.</param>
/// <param name="history">The conversations' recent messages.</param>
/// <param name="time">The clock.</param>
/// <param name="logger">The log.</param>
internal sealed class BookingAgent(IChatClient chat, ConversationHistory history, TimeProvider time, ILogger<BookingAgent> logger)
{
    /// <summary>
    /// How the message that asks the model to fix its answer begins.
    /// </summary>
    internal const string RepairPrompt = "Some A2UI messages in your answer were not sent:";

    /// <summary>
    /// How many times the model may fix an answer that had messages the renderer would reject.
    /// </summary>
    private const int MaxRepairs = 1;

    /// <summary>
    /// The longest answer kept in the history, in characters.
    /// </summary>
    private const int MaxKeptAnswerLength = 64_000;

    /// <summary>
    /// The longest text from the renderer passed to the model, in characters.
    /// </summary>
    private const int MaxRendererTextLength = 500;

    /// <summary>
    /// The longest user message passed to the model and kept in the history, in characters. With the history's own
    /// limits this bounds the memory a conversation holds.
    /// </summary>
    internal const int MaxUserTextLength = 4000;

    /// <summary>
    /// How long to wait for the renderer's time zone.
    /// </summary>
    private static readonly TimeSpan s_timeZoneTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The system prompt, built once.
    /// </summary>
    private static readonly string s_systemPrompt = BuildSystemPrompt();

    /// <summary>
    /// Gets the system prompt the model is given.
    /// </summary>
    internal static string SystemPrompt => s_systemPrompt;

    /// <summary>
    /// Runs one turn.
    /// </summary>
    /// <param name="turn">The turn.</param>
    /// <param name="cancellationToken">Cancelled when the client goes away.</param>
    public async Task RunTurnAsync(A2UIAgentTurn turn, CancellationToken cancellationToken)
    {
        List<string> notes = [];
        if (turn.Text is { Length: > 0 } text)
        {
            notes.Add(Shorten(text, MaxUserTextLength));
        }

        foreach (A2UIRendererAction action in turn.Actions)
        {
            switch (action.Name)
            {
                case BookingSurfaces.BookAction:
                    await BookAsync(turn, action, cancellationToken);
                    break;
                case BookingSurfaces.StartBookingAction:
                    notes.Add("I'd like to book a table.");
                    break;
                default:
                    notes.Add($"I pressed a button that sent the action \"{Shorten(action.Name)}\" from the surface \"{action.SurfaceId}\", with this context: {Shorten(action.Context.ToJsonString())}");
                    break;
            }
        }

        foreach (A2UIRendererError error in turn.Errors)
        {
            logger.LogInformation("The renderer reported {Code} for the surface {SurfaceId} at {Path}.", error.Code, error.SurfaceId, error.Path);
            notes.Add($"The renderer reported an error: {error.Code} at {error.Path ?? "/"} on the surface \"{error.SurfaceId}\": {Shorten(error.Message)}");
        }

        if (notes.Count > 0)
        {
            await AnswerAsync(turn, Shorten(string.Join('\n', notes), MaxUserTextLength), cancellationToken);
        }
    }

    /// <summary>
    /// Asks the model, streams its answer to the renderer, and lets it fix an answer the renderer would reject.
    /// </summary>
    /// <param name="turn">The turn.</param>
    /// <param name="userText">What the user said or did.</param>
    /// <param name="cancellationToken">Cancels the answer.</param>
    private async Task AnswerAsync(A2UIAgentTurn turn, string userText, CancellationToken cancellationToken)
    {
        List<ChatMessage> messages = [new(ChatRole.System, s_systemPrompt), .. history.Get(turn.ConversationId)];
        int newMessages = messages.Count;
        messages.Add(new ChatMessage(ChatRole.User, userText));
        for (int attempt = 0; ; attempt++)
        {
            StringBuilder answer = new();
            IReadOnlyList<A2UIErrorPart> problems = await turn.SendModelResponseAsync(StreamAsync([.. messages], answer, cancellationToken), cancellationToken: cancellationToken);
            messages.Add(new ChatMessage(ChatRole.Assistant, answer.Length <= MaxKeptAnswerLength ? answer.ToString() : answer.ToString(0, MaxKeptAnswerLength)));
            if (problems.Count == 0 || attempt == MaxRepairs)
            {
                if (problems.Count > 0)
                {
                    logger.LogWarning("The model's answer still had {Count} messages the renderer would reject.", problems.Count);
                }

                break;
            }

            StringBuilder repair = new(RepairPrompt);
            foreach (A2UIErrorPart problem in problems)
            {
                repair.Append("\n- ").Append(problem.Error);
            }

            repair.Append("\nSend corrected messages for what is missing. Do not repeat the messages that were sent.");
            messages.Add(new ChatMessage(ChatRole.User, repair.ToString()));
        }

        history.Add(turn.ConversationId, [.. messages[newMessages..]]);
    }

    /// <summary>
    /// Streams the model's answer as text chunks, keeping a copy.
    /// </summary>
    /// <returns>The chunks.</returns>
    /// <param name="messages">The conversation to send.</param>
    /// <param name="answer">Receives the whole answer.</param>
    /// <param name="cancellationToken">Cancels the stream.</param>
    private async IAsyncEnumerable<string> StreamAsync(List<ChatMessage> messages, StringBuilder answer, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (ChatResponseUpdate update in chat.GetStreamingResponseAsync(messages, cancellationToken: cancellationToken))
        {
            string text = update.Text;
            if (text.Length > 0)
            {
                if (answer.Length < MaxKeptAnswerLength)
                {
                    answer.Append(text);
                }

                yield return text;
            }
        }
    }

    /// <summary>
    /// Handles the <c>book</c> action: checks the request, asks the renderer for the user's time zone, and replaces
    /// the form with a confirmation. A request that cannot be booked gets its reason on the form instead.
    /// </summary>
    /// <param name="turn">The turn.</param>
    /// <param name="action">The action.</param>
    /// <param name="cancellationToken">Cancels the booking.</param>
    private async Task BookAsync(A2UIAgentTurn turn, A2UIRendererAction action, CancellationToken cancellationToken)
    {
        DateOnly today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        string? name = BookingRules.ReadName(action.Context["name"]);
        int guests = 0;
        DateOnly day = default;
        string? problem = name is null
            ? "Enter the name for the booking."
            : BookingRules.Check(action.Context["date"], action.Context["guests"], today, out day, out guests);
        if (problem is not null)
        {
            await turn.SendAsync(A2UIMessages.UpdateDataModel(action.SurfaceId, BookingSurfaces.ProblemPath, problem), cancellationToken);
            return;
        }

        string timeZone = await GetTimeZoneAsync(turn, action.SurfaceId, cancellationToken);

        // ReadName returned a name, or the problem above was set.
        foreach (JsonObject message in BookingSurfaces.Confirmation(action.SurfaceId, name!, day, guests, timeZone))
        {
            await turn.SendAsync(message, cancellationToken);
        }

        history.Add(
            turn.ConversationId,
            new ChatMessage(ChatRole.Assistant, $"I booked a table for {guests} on {day:yyyy-MM-dd} and replaced the surface \"{action.SurfaceId}\" with a confirmation."));
    }

    /// <summary>
    /// Asks the renderer for the user's time zone. A renderer without the function, an answer that is not a known
    /// time zone, and a renderer that does not answer in time all give UTC.
    /// </summary>
    /// <returns>The time zone's id.</returns>
    /// <param name="turn">The turn.</param>
    /// <param name="surfaceId">The surface the booking came from.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    private async Task<string> GetTimeZoneAsync(A2UIAgentTurn turn, string surfaceId, CancellationToken cancellationToken)
    {
        try
        {
            JsonNode? zone = await turn.CallRendererFunctionAsync(
                new A2UICall(SampleCatalog.GetTimeZone) { CatalogId = SampleCatalog.CatalogId },
                surfaceId,
                s_timeZoneTimeout,
                cancellationToken);
            if (zone is JsonValue value && value.TryGetValue(out string? id) && id.Length <= 64 && TimeZoneInfo.TryFindSystemTimeZoneById(id, out _))
            {
                return id;
            }

            logger.LogInformation("The renderer answered getTimeZone with something that is not a time zone.");
        }
        catch (A2UIException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("The renderer did not tell the time zone: {Code}.", ex.Code);
        }

        return "UTC";
    }

    /// <summary>
    /// Shortens text from the renderer before it goes to the model.
    /// </summary>
    /// <returns>The text, at most <paramref name="maxLength"/> characters and a mark that it was cut.</returns>
    /// <param name="text">The text.</param>
    /// <param name="maxLength">The most characters kept.</param>
    private static string Shorten(string text, int maxLength = MaxRendererTextLength) => text.Length <= maxLength ? text : text[..maxLength] + "…";

    /// <summary>
    /// Builds the system prompt: the restaurant's rules and how to show the form, then the A2UI part with the booking
    /// form as its example.
    /// </summary>
    /// <returns>The prompt.</returns>
    private static string BuildSystemPrompt()
    {
        A2UIPromptBuilder prompt = new([BasicCatalog.Create(), SampleCatalog.Create()]);
        prompt.Examples.Add(BookingSurfaces.Form());
        return $"""
            You are the booking assistant of a small sample restaurant. You talk to the user in a Blazor app that renders A2UI.

            - When the user wants to book a table, show the booking form on the surface "{BookingSurfaces.BookingId}", as in the example. Keep its Book button's action "{BookingSurfaces.BookAction}" with "name", "date" and "guests" in its context; the app handles that action itself.
            - The restaurant is closed on Mondays and one table seats at most {BookingRules.MaxGuests} people. Show the result of {SampleCatalog.CheckAvailability} under the form's inputs, so the user sees whether a table is free.
            - To show a surface again, delete it first and then create it.
            - Keep your text short and friendly.

            {prompt.Build()}
            """;
    }
}
