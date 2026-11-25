namespace BlazorA2UI.Agent;

/// <summary>
/// What a component does when the user uses it: send an event to the agent, or run a function.
/// </summary>
public sealed class A2UIAction
{
    /// <summary>
    /// The action's wire form.
    /// </summary>
    private readonly JsonObject _json;

    /// <summary>
    /// Creates an action from its wire form.
    /// </summary>
    /// <param name="json">The wire form, owned by this action.</param>
    private A2UIAction(JsonObject json) => _json = json;

    /// <summary>
    /// Creates an action that sends an event to the agent.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    /// <param name="name">The event name the agent receives.</param>
    /// <param name="context">Values sent with the event. Use literals for fixed ids and bindings for user input.</param>
    /// <param name="userMessage">A human-readable description of what the user did, resolved by the renderer.</param>
    public static A2UIAction Event(string name, IReadOnlyDictionary<string, A2UIValue>? context = null, A2UIValue? userMessage = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        JsonObject body = new() { ["name"] = name };
        if (userMessage is not null)
        {
            body["userMessage"] = userMessage.ToJson();
        }

        if (context is { Count: > 0 })
        {
            JsonObject values = new();
            foreach ((string key, A2UIValue value) in context)
            {
                values[key] = value.ToJson();
            }

            body["context"] = values;
        }

        return new A2UIAction(new JsonObject { ["event"] = body });
    }

    /// <summary>
    /// Creates an action that runs a function, such as <c>openUrl</c>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="call"/> is null.</exception>
    /// <param name="call">The call.</param>
    public static A2UIAction FunctionCall(A2UICall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return new A2UIAction(new JsonObject { ["functionCall"] = call.ToJson() });
    }

    /// <summary>
    /// Returns the action's wire form. Each call returns a new copy.
    /// </summary>
    public JsonObject ToJson() => (JsonObject)_json.DeepClone();
}
