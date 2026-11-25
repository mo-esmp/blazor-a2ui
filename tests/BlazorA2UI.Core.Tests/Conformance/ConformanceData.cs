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
    private static readonly Dictionary<string, List<JsonObject>> Cache = new();

    public static IReadOnlyList<JsonObject> Load(string file)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(file, out var cached))
            {
                return cached;
            }

            var path = Path.Combine(AppContext.BaseDirectory, "Conformance", "data", file);
            var yaml = new YamlStream();
            using (var reader = new StreamReader(path))
            {
                yaml.Load(reader);
            }

            var root = (JsonArray)ToJson(yaml.Documents[0].RootNode)!;
            var cases = root.OfType<JsonObject>().ToList();
            Cache[file] = cases;
            return cases;
        }
    }

    public static JsonObject Case(string file, string name) =>
        Load(file).Single(c => c["name"]!.GetValue<string>() == name);

    public static IEnumerable<object[]> Names(string file, Func<JsonObject, bool> filter) =>
        Load(file).Where(filter).Select(c => new object[] { c["name"]!.GetValue<string>() });

    private static JsonNode? ToJson(YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode map:
            {
                var obj = new JsonObject();
                foreach (var (key, value) in map.Children)
                {
                    obj[((YamlScalarNode)key).Value!] = ToJson(value);
                }

                return obj;
            }

            case YamlSequenceNode seq:
            {
                var arr = new JsonArray();
                foreach (var item in seq.Children)
                {
                    arr.Add(ToJson(item));
                }

                return arr;
            }

            case YamlScalarNode scalar:
            {
                var text = scalar.Value ?? string.Empty;
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

                if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
                {
                    return JsonValue.Create(l);
                }

                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
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
