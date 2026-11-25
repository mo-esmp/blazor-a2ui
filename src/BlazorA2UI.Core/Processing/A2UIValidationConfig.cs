namespace BlazorA2UI.Processing;

/// <summary>
/// Completeness rules applied to each component update. A renderer normally runs without them
/// (<see cref="A2UIProcessorOptions.Validation"/> is null), so components can stream in any order.
/// Agents and tests validating a finished payload use <see cref="Strict"/>.
/// </summary>
public sealed record A2UIValidationConfig
{
    /// <summary>Every rule enforced: a root must exist, every reference must resolve, nothing may be unreachable, and every function must be declared.</summary>
    public static A2UIValidationConfig Strict { get; } = new();

    /// <summary>Every rule relaxed.</summary>
    public static A2UIValidationConfig Relaxed { get; } = new()
    {
        AllowOrphanComponents = true,
        AllowDanglingReferences = true,
        AllowMissingRoot = true,
        AllowUnknownElements = true,
    };

    /// <summary>Allow components that are not reachable from <c>root</c>.</summary>
    public bool AllowOrphanComponents { get; init; }

    /// <summary>Allow child references to components that do not exist (yet).</summary>
    public bool AllowDanglingReferences { get; init; }

    /// <summary>Allow a surface without a <c>root</c> component.</summary>
    public bool AllowMissingRoot { get; init; }

    /// <summary>
    /// Allow function calls to functions the resolved catalog does not declare (they are then
    /// routed to the agent with <c>callAgentFunction</c>).
    /// </summary>
    public bool AllowUnknownElements { get; init; }
}
