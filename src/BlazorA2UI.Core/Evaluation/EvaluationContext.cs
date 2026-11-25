using BlazorA2UI.Data;
using BlazorA2UI.Processing;

namespace BlazorA2UI.Evaluation;

/// <summary>
/// Where an expression is evaluated: a surface's data model and the scope inside it.
/// Template children get a child scope whose relative paths resolve against the list item.
/// </summary>
public sealed class EvaluationContext
{
    private EvaluationContext(Surface? surface, DataModel data, string path, IReadOnlyList<int> indices)
    {
        Surface = surface;
        Data = data;
        Path = path;
        Indices = indices;
    }

    /// <summary>The surface, or null for a surface-independent evaluation.</summary>
    public Surface? Surface { get; }

    public DataModel Data { get; }

    /// <summary>The absolute path relative bindings resolve against.</summary>
    public string Path { get; }

    /// <summary>Template iteration indices, outermost first. Empty in the root scope.</summary>
    public IReadOnlyList<int> Indices { get; }

    /// <summary>True inside a list template (where <c>@index</c> is defined).</summary>
    public bool InCollectionScope => Indices.Count > 0;

    public static EvaluationContext ForSurface(Surface surface) => new(surface, surface.DataModel, "/", []);

    /// <summary>A context over a standalone data model (an empty one when omitted).</summary>
    public static EvaluationContext Detached(DataModel? data = null, string path = "/") =>
        new(null, data ?? new DataModel(), JsonPointer.Normalize(path), []);

    /// <summary>Returns the scope of one item of a template list bound at <paramref name="listPath"/>.</summary>
    public EvaluationContext ForTemplateItem(string listPath, int index)
    {
        var absoluteList = JsonPointer.Resolve(listPath, Path);
        var itemPath = JsonPointer.Resolve(index.ToString(System.Globalization.CultureInfo.InvariantCulture), absoluteList);
        return new EvaluationContext(Surface, Data, itemPath, [.. Indices, index]);
    }

    /// <summary>Resolves a binding path against this scope.</summary>
    public string Resolve(string path) => JsonPointer.Resolve(path, Path);
}
