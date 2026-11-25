namespace BlazorA2UI.Processing;

/// <summary>
/// Settings for a <see cref="MessageProcessor"/>.
/// </summary>
public sealed class A2UIProcessorOptions
{
    /// <summary>
    /// How long a renderer-initiated agent function call may wait for its answer.
    /// </summary>
    public TimeSpan AgentCallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum number of agent function calls waiting for an answer at once, per surface.
    /// </summary>
    public int MaxPendingAgentCalls { get; set; } = 64;

    /// <summary>
    /// Maximum components on one surface.
    /// </summary>
    public int MaxComponentsPerSurface { get; set; } = 5_000;

    /// <summary>
    /// Maximum depth of the rendered component tree.
    /// </summary>
    public int MaxTreeDepth { get; set; } = 50;

    /// <summary>
    /// When true (the default), errors the renderer reports are also sent to the agent as
    /// <c>error</c> messages so a model can correct itself.
    /// </summary>
    public bool SendErrorsToAgent { get; set; } = true;

    /// <summary>
    /// Completeness rules for component updates. Null (the default) suits a streaming renderer:
    /// components may arrive in any order and undeclared functions are routed to the agent.
    /// </summary>
    public A2UIValidationConfig? Validation { get; set; }

    /// <summary>
    /// Host services passed to function implementations (for example an <c>IA2UIUrlLauncher</c>).
    /// </summary>
    public IServiceProvider? Services { get; set; }
}
