using System.Globalization;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

namespace BlazorA2UI.Tests.Conformance;

/// <summary>
/// Loads cases from the language-agnostic A2UI conformance suite (vendored under
/// Conformance/data, Apache License 2.0, from github.com/a2ui-project/a2ui).
/// </summary>
internal static class ConformanceData
{
    /// <summary>
    /// Parsed suite files, by file name.
    /// </summary>
    private static readonly Dictionary<string, List<JsonObject>> s_cache = new();

    /// <summary>
    /// Loads every case of a vendored suite file, parsing it once.
    /// </summary>
    public static IReadOnlyList<JsonObject> Load(string file)
    {
        lock (s_cache)
        {
            if (s_cache.TryGetValue(file, out List<JsonObject>? cached))
            {
                return cached;
            }

            string path = Path.Combine(AppContext.BaseDirectory, "Conformance", "data", file);
            YamlStream yaml = new();
            using (StreamReader reader = new(path))
            {
                yaml.Load(reader);
            }

            JsonArray root = (JsonArray)ToJson(yaml.Documents[0].RootNode).Required();
            List<JsonObject> cases = root.OfType<JsonObject>().ToList();
            s_cache[file] = cases;
            return cases;
        }
    }

    /// <summary>
    /// Returns the case named <paramref name="name"/> from a suite file.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file has no case, or more than one, with that name.</exception>
    public static JsonObject Case(string file, string name) =>
        Load(file).Single(c => c["name"].Required().GetValue<string>() == name);

    /// <summary>
    /// Returns the names of the cases that pass <paramref name="filter"/>, as theory data.
    /// </summary>
    public static IEnumerable<object[]> Names(string file, Func<JsonObject, bool> filter) =>
        Load(file).Where(filter).Select(c => new object[] { c["name"].Required().GetValue<string>() });

    /// <summary>
    /// Converts a YAML node into JSON, reading plain scalars as numbers, booleans, null or strings the way YAML 1.2 does.
    /// </summary>
    private static JsonNode? ToJson(YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode map:
                {
                    JsonObject obj = new();
                    foreach ((YamlNode? key, YamlNode? value) in map.Children)
                    {
                        obj[((YamlScalarNode)key).Value.Required()] = ToJson(value);
                    }

                    return obj;
                }

            case YamlSequenceNode seq:
                {
                    JsonArray arr = new();
                    foreach (YamlNode item in seq.Children)
                    {
                        arr.Add(ToJson(item));
                    }

                    return arr;
                }

            case YamlScalarNode scalar:
                {
                    string text = scalar.Value ?? string.Empty;
                    if (scalar.Style is not YamlDotNet.Core.ScalarStyle.Plain)
                    {
                        return JsonValue.Create(text);
                    }

                    switch (text)
                    {
                        case "null" or "~" or "":
                            return null;
                        case "true":
                            return JsonValue.Create(true);
                        case "false":
                            return JsonValue.Create(false);
                        case ".nan" or ".NaN" or ".NAN":
                            return JsonValue.Create(double.NaN);
                        case ".inf" or "+.inf" or ".Inf" or "+.Inf":
                            return JsonValue.Create(double.PositiveInfinity);
                        case "-.inf" or "-.Inf":
                            return JsonValue.Create(double.NegativeInfinity);
                    }

                    if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l))
                    {
                        return JsonValue.Create(l);
                    }

                    if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                        && (char.IsAsciiDigit(text[0]) || text[0] is '-' or '+' or '.'))
                    {
                        return JsonValue.Create(d);
                    }

                    return JsonValue.Create(text);
                }

            default:
                return null;
        }
    }
}
