using System.Globalization;
using System.Text.Json.Nodes;
using BlazorA2UI.Functions;

namespace BlazorA2UI.Catalogs;

/// <summary>
/// The A2UI v1.0 basic catalog, with renderer-side implementations of its functions.
/// </summary>
public static class BasicCatalog
{
    /// <summary>
    /// The embedded basic catalog document, parsed once on first use.
    /// </summary>
    private static readonly Lazy<JsonObject> s_document = new(() =>
    {
        using Stream stream = typeof(BasicCatalog).Assembly.GetManifestResourceStream("BlazorA2UI.Catalogs.Basic.catalog.json")
                           ?? throw new InvalidOperationException("The basic catalog resource is missing.");
        // The embedded catalog is a JSON object, so Parse never returns null for it.
        return (JsonObject)JsonNode.Parse(stream)!;
    });

    /// <summary>
    /// The catalog id of the v1.0 basic catalog.
    /// </summary>
    public const string CatalogId = A2UIProtocol.BasicCatalogId;

    /// <summary>
    /// The basic catalog's JSON Schema document (a copy).
    /// </summary>
    public static JsonObject GetDocument() => (JsonObject)s_document.Value.DeepClone();

    /// <summary>
    /// Creates the basic catalog with every function implemented.
    /// </summary>
    /// <param name="culture">Culture for number, currency, date and plural formatting. Defaults to the current culture.</param>
    public static Catalog Create(CultureInfo? culture = null) =>
        Catalog.FromJson(s_document.Value).WithFunctions(BasicFunctions.Create(culture));
}
