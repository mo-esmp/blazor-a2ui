namespace BlazorA2UI.Functions;

/// <summary>URL checks shared by <c>openUrl</c> and the media components.</summary>
public static class UrlSafety
{
    /// <summary>
    /// Validates a navigation target for <c>openUrl</c>: an absolute http(s) URL with a host,
    /// or a same-host relative path. Returns the trimmed URL.
    /// </summary>
    /// <exception cref="A2UIExpressionException">The URL is empty or uses another scheme.</exception>
    public static string ValidateNavigationUrl(string? url)
    {
        string trimmed = Clean(url);
        if (trimmed.Length == 0)
        {
            throw new A2UIExpressionException("openUrl needs a non-empty url.");
        }

        if (IsRelativeSameHost(trimmed))
        {
            return trimmed;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri)
            && uri.Scheme is "http" or "https"
            && !string.IsNullOrEmpty(uri.Host)
            && HasAuthority(trimmed))
        {
            return trimmed;
        }

        throw new A2UIExpressionException("openUrl only opens http and https URLs.");
    }

    /// <summary>
    /// The media URL rules of the basic catalog implementation guide: trimmed, scheme
    /// <c>http</c>, <c>https</c> or <c>data</c>, http(s) written with <c>//host</c>, and
    /// relative paths that stay on the same host. Returns null when the URL is rejected.
    /// </summary>
    public static string? ValidateMediaUrl(string? url, bool allowData = true)
    {
        string trimmed = Clean(url);
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (IsRelativeSameHost(trimmed))
        {
            return trimmed;
        }

        int colon = trimmed.IndexOf(':');
        if (colon <= 0)
        {
            return null;
        }

        string scheme = trimmed[..colon].ToLowerInvariant();
        if (scheme == "data")
        {
            return allowData ? trimmed : null;
        }

        if (scheme is not ("http" or "https") || !HasAuthority(trimmed))
        {
            return null;
        }

        return Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) && !string.IsNullOrEmpty(uri.Host) ? trimmed : null;
    }

    private static string Clean(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return string.Empty;
        }

        string trimmed = url.Trim().Trim(Enumerable.Range(0, 0x21).Select(c => (char)c).ToArray());

        // Browsers drop tab, newline and carriage return anywhere in a URL.
        return trimmed.Replace("\t", string.Empty).Replace("\n", string.Empty).Replace("\r", string.Empty);
    }

    private static bool HasAuthority(string url)
    {
        int colon = url.IndexOf(':');
        ReadOnlySpan<char> rest = url.AsSpan(colon + 1);
        return rest.Length > 2 && rest[0] == '/' && rest[1] == '/' && rest[2] is not ('/' or '\\');
    }

    private static bool IsRelativeSameHost(string url)
    {
        int colon = url.IndexOf(':');
        int firstDelimiter = url.IndexOfAny(['/', '?', '#']);
        bool hasScheme = colon > 0 && (firstDelimiter < 0 || colon < firstDelimiter)
                        && url[..colon].All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '+' or '-' or '.')
                        && char.IsAsciiLetter(url[0]);
        if (hasScheme)
        {
            return false;
        }

        string normalized = url.Replace('\\', '/');
        return !normalized.StartsWith("//", StringComparison.Ordinal);
    }
}
