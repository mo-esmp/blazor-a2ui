namespace BlazorA2UI.Media;

/// <summary>
/// The one path from a model-supplied media URL to the page: through the host's resolver, and only as an absolute http or https URL.
/// </summary>
internal static class MediaUrls
{
    /// <summary>
    /// Asks the resolver about a URL and returns the URL to put in the page, or null to show a blocked placeholder.
    /// </summary>
    /// <param name="resolver">The host's resolver.</param>
    /// <param name="kind">What the URL is for.</param>
    /// <param name="url">The model's URL, or null when the component has none.</param>
    /// <param name="surface">The surface that shows it.</param>
    /// <param name="component">The component that shows it.</param>
    /// <returns>The resolved URL; null when there is no URL, the resolver blocks it, or it returns anything but http or https.</returns>
    internal static string? Resolve(IA2UIMediaResolver resolver, A2UIMediaKind kind, string? url, Surface surface, ComponentInstance component)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        Uri? resolved = resolver.Resolve(new A2UIMediaRequest(kind, url, surface.SurfaceId, component.Id));
        return resolved is { IsAbsoluteUri: true, Scheme: "https" or "http" } ? resolved.AbsoluteUri : null;
    }
}
