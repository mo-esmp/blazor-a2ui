namespace BlazorA2UI.Agent;

/// <summary>
/// A validation rule on an input component (an item of <c>@checks</c>).
/// </summary>
public sealed class A2UICheck
{
    /// <summary>
    /// Creates a check.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="condition"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="condition"/> is a literal; it must be a binding or a call.</exception>
    /// <param name="condition">A binding or call that evaluates to a boolean or a validation result.</param>
    /// <param name="message">The message shown when the check fails and the result carries none.</param>
    public A2UICheck(A2UIValue condition, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (condition.Kind == A2UIValueKind.Literal)
        {
            throw new ArgumentException("A check condition must be a binding or a function call.", nameof(condition));
        }

        Condition = condition;
        Message = message;
    }

    /// <summary>
    /// Gets the condition.
    /// </summary>
    public A2UIValue Condition { get; }

    /// <summary>
    /// Gets the fallback message.
    /// </summary>
    public string? Message { get; }

    /// <summary>
    /// Returns the check's wire form.
    /// </summary>
    public JsonObject ToJson()
    {
        JsonObject check = new() { ["condition"] = Condition.ToJson() };
        if (Message is not null)
        {
            check["message"] = Message;
        }

        return check;
    }
}
