namespace BlazorA2UI.Media;

/// <summary>
/// A model-supplied media URL that a component wants to show.
/// </summary>
/// <param name="Kind">What the URL is for.</param>
/// <param name="Url">The URL exactly as the model wrote it. It is untrusted.</param>
/// <param name="SurfaceId">The surface that shows it.</param>
/// <param name="ComponentId">The component that shows it.</param>
public sealed record A2UIMediaRequest(A2UIMediaKind Kind, string Url, string SurfaceId, string ComponentId);
