namespace BlazorA2UI.Media;

/// <summary>
/// The media allowlist the default <see cref="IA2UIMediaResolver"/> uses. It is empty by default, which blocks all media.
/// </summary>
public sealed class A2UIMediaOptions
{
    /// <summary>
    /// The allowed URL prefixes, by media kind.
    /// </summary>
    private readonly List<(Uri Prefix, A2UIMediaKind[] Kinds)> _prefixes = [];

    /// <summary>
    /// Gets the allowed prefixes and the media kinds each one allows.
    /// </summary>
    internal IReadOnlyList<(Uri Prefix, A2UIMediaKind[] Kinds)> Prefixes => _prefixes;

    /// <summary>
    /// Allows media whose URL starts with <paramref name="prefix"/>: the same scheme, host and port, and a path
    /// inside the prefix's path. <c>https://cdn.example.com/media/</c> allows <c>https://cdn.example.com/media/a.png</c>
    /// but not <c>https://cdn.example.com/other.png</c> or <c>https://cdn.example.com.evil.test/media/a.png</c>.
    /// </summary>
    /// <param name="prefix">An absolute http or https URL with no user name, password, query or fragment.</param>
    /// <param name="kinds">The media kinds to allow; none means every kind.</param>
    /// <returns>These options, for chaining.</returns>
    /// <exception cref="ArgumentException">The prefix is not such a URL.</exception>
    public A2UIMediaOptions Allow(string prefix, params A2UIMediaKind[] kinds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        if (!Uri.TryCreate(prefix, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("https" or "http")
            || uri.UserInfo.Length > 0
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0)
        {
            throw new ArgumentException("A media prefix must be an absolute http or https URL with no user info, query or fragment.", nameof(prefix));
        }

        A2UIMediaKind[] allowed = kinds.Length == 0 ? Enum.GetValues<A2UIMediaKind>() : (A2UIMediaKind[])kinds.Clone();
        _prefixes.Add((uri, allowed));
        return this;
    }
}
