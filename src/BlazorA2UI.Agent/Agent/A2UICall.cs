namespace BlazorA2UI.Agent;

/// <summary>
/// A function call, <c>{"@call": name, "args": {...}}</c>, used as a property value, a check condition or an action.
/// </summary>
public sealed class A2UICall
{
    /// <summary>
    /// Creates a call to a function.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    /// <param name="name">The function name, such as <c>formatString</c>.</param>
    public A2UICall(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
    }

    /// <summary>
    /// Gets the function name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the named arguments. Each argument is a literal, a binding or another call.
    /// </summary>
    public Dictionary<string, A2UIValue> Args { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets the catalog that defines the function, when it is not the surface's default catalog.
    /// </summary>
    public string? CatalogId { get; init; }

    /// <summary>
    /// Returns the call's wire form.
    /// </summary>
    public JsonObject ToJson()
    {
        JsonObject call = new() { ["@call"] = Name };
        if (Args.Count > 0)
        {
            JsonObject args = new();
            foreach ((string key, A2UIValue value) in Args)
            {
                args[key] = value.ToJson();
            }

            call["args"] = args;
        }

        if (CatalogId is not null)
        {
            call["catalogId"] = CatalogId;
        }

        return call;
    }
}
