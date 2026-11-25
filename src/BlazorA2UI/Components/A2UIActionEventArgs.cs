namespace BlazorA2UI.Components;

/// <summary>
/// Describes a component action that has run, for <see cref="A2UISurface.OnAction"/>.
/// </summary>
/// <param name="SurfaceId">The surface the component belongs to.</param>
/// <param name="ComponentId">The component the user acted on.</param>
/// <param name="Context">The data scope the action resolved against.</param>
public sealed record A2UIActionEventArgs(string SurfaceId, string ComponentId, EvaluationContext Context);
