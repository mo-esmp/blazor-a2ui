using System.Collections.Frozen;

namespace BlazorA2UI.Rendering;

/// <summary>
/// The basic catalog's icon names and the rules for custom SVG paths. An icon renders as a fixed CSS class, so a
/// host styles icons with its own, locally served icon set; nothing is fetched on the model's behalf.
/// </summary>
internal static class A2UIIcons
{
    /// <summary>
    /// The longest <c>svgPath</c> accepted.
    /// </summary>
    internal const int MaxPathLength = 4096;

    /// <summary>
    /// The class for each icon name of the basic catalog.
    /// </summary>
    private static readonly FrozenDictionary<string, string> s_classes = new[]
    {
        "accountCircle", "add", "arrowBack", "arrowForward", "attachFile", "calendarToday", "call", "camera", "check",
        "close", "delete", "download", "edit", "event", "error", "fastForward", "favorite", "favoriteOff", "folder",
        "help", "home", "info", "locationOn", "lock", "lockOpen", "mail", "menu", "moreVert", "moreHoriz",
        "notificationsOff", "notifications", "pause", "payment", "person", "phone", "photo", "play", "print", "refresh",
        "rewind", "search", "send", "settings", "share", "shoppingCart", "skipNext", "skipPrevious", "star", "starHalf",
        "starOff", "stop", "upload", "visibility", "visibilityOff", "volumeDown", "volumeMute", "volumeOff", "volumeUp",
        "warning",
    }.ToFrozenDictionary(name => name, name => "a2ui-icon-" + name, StringComparer.Ordinal);

    /// <summary>
    /// The class for an icon name, or null when the name is not one of the catalog's icons.
    /// </summary>
    internal static string? ClassFor(JsonNode? name) =>
        A2UIValues.AsString(name) is { } key && s_classes.TryGetValue(key, out string? css) ? css : null;

    /// <summary>
    /// Returns <paramref name="path"/> when it is SVG path data (commands, numbers, signs, separators), or null.
    /// Path data cannot load anything, and the check keeps any other text out of the attribute.
    /// </summary>
    internal static string? ValidPath(string? path)
    {
        if (path is null || path.Length is 0 or > MaxPathLength)
        {
            return null;
        }

        foreach (char ch in path)
        {
            bool allowed = ch is (>= '0' and <= '9') or '.' or ',' or '-' or '+' or ' ' or '\t' or '\n' or '\r'
                || "MmLlHhVvCcSsQqTtAaZzEe".Contains(ch, StringComparison.Ordinal);
            if (!allowed)
            {
                return null;
            }
        }

        return path;
    }
}
