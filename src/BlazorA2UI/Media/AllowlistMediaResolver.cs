using Microsoft.Extensions.Options;

namespace BlazorA2UI.Media;

/// <summary>
/// The default <see cref="IA2UIMediaResolver"/>: allows a URL only when it falls under a prefix in <see cref="A2UIMediaOptions"/>.
/// </summary>
/// <param name="options">The configured allowlist.</param>
internal sealed class AllowlistMediaResolver(IOptions<A2UIMediaOptions> options) : IA2UIMediaResolver
{
    /// <summary>
    /// The longest URL considered; longer ones are blocked.
    /// </summary>
    internal const int MaxUrlLength = 2048;

    /// <inheritdoc />
    public Uri? Resolve(A2UIMediaRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Url.Length is 0 or > MaxUrlLength
            || !Uri.TryCreate(request.Url, UriKind.Absolute, out Uri? url)
            || url.Scheme is not ("https" or "http")
            || url.UserInfo.Length > 0
            || HasEncodedSeparator(url.AbsolutePath))
        {
            return null;
        }

        foreach ((Uri prefix, A2UIMediaKind[] kinds) in options.Value.Prefixes)
        {
            if (Array.IndexOf(kinds, request.Kind) >= 0 && IsUnder(url, prefix))
            {
                return url;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a path holds an encoded dot, slash or backslash. <see cref="Uri"/> removes literal <c>..</c> segments,
    /// but a server may decode <c>%2e%2e</c> or <c>%2f</c> after the prefix check and climb out of the prefix,
    /// so such paths are blocked.
    /// </summary>
    internal static bool HasEncodedSeparator(string path) =>
        path.Contains("%2e", StringComparison.OrdinalIgnoreCase)
        || path.Contains("%2f", StringComparison.OrdinalIgnoreCase)
        || path.Contains("%5c", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="url"/> has the prefix's scheme, host and port, and a path inside the prefix's path.
    /// Paths are compared after normalization, so <c>..</c> cannot climb out of the prefix.
    /// </summary>
    internal static bool IsUnder(Uri url, Uri prefix)
    {
        if (!string.Equals(url.Scheme, prefix.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(url.IdnHost, prefix.IdnHost, StringComparison.OrdinalIgnoreCase)
            || url.Port != prefix.Port)
        {
            return false;
        }

        string prefixPath = prefix.AbsolutePath;
        string path = url.AbsolutePath;
        if (prefixPath.EndsWith('/'))
        {
            return path.StartsWith(prefixPath, StringComparison.Ordinal);
        }

        return path == prefixPath || path.StartsWith(prefixPath + "/", StringComparison.Ordinal);
    }
}
