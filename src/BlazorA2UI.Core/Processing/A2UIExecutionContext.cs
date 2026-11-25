namespace BlazorA2UI.Processing;

/// <summary>
/// Settings for processing one batch of agent messages, in place of the processor's defaults.
/// </summary>
public sealed class A2UIExecutionContext
{
    /// <summary>
    /// Gets whether the messages are processed inside a user gesture, for example the agent's answer to a
    /// button press. Only then may a <c>callRendererFunction</c> run a function that requires user activation.
    /// </summary>
    public bool IsUserActivated { get; init; }

    /// <summary>
    /// Gets the validation rules for this call only, or null to use <see cref="A2UIProcessorOptions.Validation"/>.
    /// </summary>
    public A2UIValidationConfig? Validation { get; init; }
}
