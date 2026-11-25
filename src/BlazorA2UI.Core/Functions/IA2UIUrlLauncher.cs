namespace BlazorA2UI.Functions;

/// <summary>
/// Opens URLs for the basic catalog's <c>openUrl</c> function. The core validates the
/// scheme (http and https only) before calling it; a launcher may apply stricter policy.
/// </summary>
public interface IA2UIUrlLauncher
{
    /// <summary>
    /// Opens <paramref name="url"/>, an absolute http(s) URL or a same-origin relative path.
    /// </summary>
    ValueTask OpenAsync(string url, CancellationToken cancellationToken = default);
}
