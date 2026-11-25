using System.Text.Json.Nodes;
using BlazorA2UI.Catalogs;

namespace BlazorA2UI.Model;

/// <summary>One component of a surface, as last sent by the agent.</summary>
public sealed class ComponentInstance
{
    internal ComponentInstance(
        string id,
        string type,
        Catalog catalog,
        ComponentDefinition definition,
        JsonObject properties,
        JsonObject? accessibility,
        JsonObject? metadata,
        JsonArray? checks,
        string? explicitCatalogId)
    {
        Id = id;
        Type = type;
        Catalog = catalog;
        Definition = definition;
        Properties = properties;
        Accessibility = accessibility;
        Metadata = metadata;
        Checks = checks;
        ExplicitCatalogId = explicitCatalogId;
    }

    public string Id { get; }

    /// <summary>The component type name (the <c>component</c> key).</summary>
    public string Type { get; }

    /// <summary>The catalog the component resolved to.</summary>
    public Catalog Catalog { get; }

    public ComponentDefinition Definition { get; }

    /// <summary>The component's own (catalog-declared) properties, unevaluated.</summary>
    public JsonObject Properties { get; }

    /// <summary><c>@accessibility</c>, unevaluated.</summary>
    public JsonObject? Accessibility { get; }

    /// <summary><c>@metadata</c>.</summary>
    public JsonObject? Metadata { get; }

    /// <summary><c>@checks</c>, unevaluated.</summary>
    public JsonArray? Checks { get; }

    /// <summary>The <c>@catalogId</c> the agent wrote, if any.</summary>
    public string? ExplicitCatalogId { get; }

    /// <summary>The raw value of a property, or null.</summary>
    public JsonNode? this[string property] => Properties[property];

    /// <summary>Ids of the components this one references through static child properties.</summary>
    public IEnumerable<string> StaticChildIds(bool includeTemplates = true) =>
        ChildReferences(includeTemplates).Select(r => r.Id);

    /// <summary>
    /// The components this one references, each with the JSON Pointer (relative to the component)
    /// of the reference, such as <c>/children/0</c> or <c>/tabs/1/child</c>.
    /// </summary>
    public IEnumerable<(string Id, string Pointer)> ChildReferences(bool includeTemplates = true)
    {
        foreach (var (prop, kind) in Definition.ChildProperties)
        {
            var value = Properties[prop];
            var basePointer = "/" + Data.JsonPointer.Escape(prop);
            switch (kind)
            {
                case ChildReferenceKind.Single when value is JsonValue v && v.TryGetValue<string>(out var id):
                    yield return (id, basePointer);
                    break;
                case ChildReferenceKind.List when value is JsonArray arr:
                    for (var i = 0; i < arr.Count; i++)
                    {
                        if (arr[i] is JsonValue iv && iv.TryGetValue<string>(out var childId))
                        {
                            yield return (childId, $"{basePointer}/{i}");
                        }
                    }

                    break;
                case ChildReferenceKind.List when includeTemplates
                                                  && value is JsonObject template
                                                  && template["componentId"] is JsonValue t
                                                  && t.TryGetValue<string>(out var templateId):
                    yield return (templateId, basePointer + "/componentId");
                    break;
                case ChildReferenceKind.NestedSingle when value is JsonArray items:
                    var nestedKey = Definition.NestedChildKeys.GetValueOrDefault(prop, "child");
                    for (var i = 0; i < items.Count; i++)
                    {
                        if (items[i] is JsonObject item && item[nestedKey] is JsonValue nv && nv.TryGetValue<string>(out var nestedId))
                        {
                            yield return (nestedId, $"{basePointer}/{i}/{Data.JsonPointer.Escape(nestedKey)}");
                        }
                    }

                    break;
            }
        }
    }
}
