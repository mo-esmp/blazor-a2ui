namespace BlazorA2UI.Media;

/// <summary>
/// Decides which model-supplied media URLs the page may load. Components never put a URL into the page
/// unless the resolver returns it, so without a resolver that allows something, media render as blocked placeholders.
/// </summary>
public interface IA2UIMediaResolver
{
    /// <summary>
    /// Returns the URL to load for <paramref name="request"/>, or null to block it.
    /// </summary>
    /// <param name="request">The media the model asked for.</param>
    /// <returns>An absolute http or https URL the host trusts, or null. A returned URL with another scheme is ignored.</returns>
    Uri? Resolve(A2UIMediaRequest request);
}
