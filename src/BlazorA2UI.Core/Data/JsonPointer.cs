using System.Text;

namespace BlazorA2UI.Data;

/// <summary>
/// RFC 6901 JSON Pointer helpers, with the A2UI normalisation rules: empty
/// segments are collapsed (<c>//a///b//</c> is <c>/a/b</c>), and relative
/// paths resolve against a data context path.
/// </summary>
internal static class JsonPointer
{
    /// <summary>
    /// Segments that are rejected in data paths, so a model cannot write to prototype-like keys that a JavaScript host might interpret.
    /// </summary>
    private static readonly HashSet<string> s_forbiddenSegments = ["__proto__", "constructor", "prototype"];

    /// <summary>
    /// Splits a pointer into unescaped segments. The root is an empty list.
    /// </summary>
    public static IReadOnlyList<string> Parse(string pointer)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        List<string> segments = new();
        foreach (string raw in pointer.Split('/'))
        {
            if (raw.Length == 0)
            {
                continue;
            }

            string segment = Unescape(raw);
            if (s_forbiddenSegments.Contains(segment))
            {
                throw new A2UIDataException($"Forbidden path segment '{segment}' in '{pointer}'.");
            }

            segments.Add(segment);
        }

        return segments;
    }

    /// <summary>
    /// Builds a canonical pointer from unescaped segments.
    /// </summary>
    public static string Format(IEnumerable<string> segments)
    {
        StringBuilder sb = new();
        foreach (string segment in segments)
        {
            sb.Append('/').Append(Escape(segment));
        }

        return sb.Length == 0 ? "/" : sb.ToString();
    }

    /// <summary>
    /// Returns the canonical form of an absolute pointer.
    /// </summary>
    public static string Normalize(string pointer) => Format(Parse(pointer));

    /// <summary>
    /// Resolves <paramref name="path"/> against <paramref name="contextPath"/>.
    /// Absolute paths ignore the context; <c>""</c> and <c>"."</c> mean the context itself.
    /// </summary>
    public static string Resolve(string path, string? contextPath)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.StartsWith('/'))
        {
            return Normalize(path);
        }

        string context = string.IsNullOrEmpty(contextPath) ? "/" : contextPath;
        if (path.Length == 0 || path == ".")
        {
            return Normalize(context);
        }

        List<string> segments = new(Parse(context));
        segments.AddRange(Parse(path));
        return Format(segments);
    }

    /// <summary>
    /// True when <paramref name="ancestor"/> is <paramref name="path"/> or one of its ancestors.
    /// </summary>
    public static bool IsSameOrAncestor(IReadOnlyList<string> ancestor, IReadOnlyList<string> path)
    {
        if (ancestor.Count > path.Count)
        {
            return false;
        }

        for (int i = 0; i < ancestor.Count; i++)
        {
            if (!string.Equals(ancestor[i], path[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Escapes one key as a JSON Pointer segment (<c>~</c> becomes <c>~0</c> and <c>/</c> becomes <c>~1</c>).
    /// </summary>
    public static string Escape(string segment) => segment.Replace("~", "~0").Replace("/", "~1");

    /// <summary>
    /// Unescapes one JSON Pointer segment, reversing <see cref="Escape"/>.
    /// </summary>
    public static string Unescape(string segment) => segment.Replace("~1", "/").Replace("~0", "~");
}
