using System.Text.Json;
using System.Text.Json.Nodes;
using BlazorA2UI.Evaluation;
using BlazorA2UI.Processing;
using Xunit;

namespace BlazorA2UI.Tests.Conformance;

/// <summary>Assertions shared by the conformance harnesses.</summary>
internal static class ConformanceAssert
{
    /// <summary>Checks an error against a case's <c>expectError</c> category and message fragment.</summary>
    public static void Error(JsonObject expected, Exception? actual, string name)
    {
        var category = Str(expected["category"]);
        Assert.True(actual is not null, $"{name}: expected a {category} error but none was raised");
        var code = (actual as A2UIException)?.Code;
        string[] accepted = category switch
        {
            "DataError" => [A2UIErrorCodes.DataError],
            "ExpressionError" => [A2UIErrorCodes.ExpressionError],
            "ParseError" => [A2UIErrorCodes.ExpressionError],
            "CatalogError" or "A2uiCatalogError" => [A2UIErrorCodes.CatalogError],
            "IntegrityError" => [A2UIErrorCodes.IntegrityError, A2UIErrorCodes.RecursionLimitExceeded],
            "RecursionError" => [A2UIErrorCodes.RecursionLimitExceeded],
            "ValidationError" =>
            [
                A2UIErrorCodes.ValidationFailed, A2UIErrorCodes.InvalidReservedKey, A2UIErrorCodes.ExpressionError,
                A2UIErrorCodes.UnallowedChild, A2UIErrorCodes.UnallowedParent, A2UIErrorCodes.IntegrityError,
                A2UIErrorCodes.RecursionLimitExceeded, A2UIErrorCodes.InvalidFunctionCall,
            ],
            null => [code ?? string.Empty],
            _ => [code ?? string.Empty],
        };
        Assert.True(accepted.Contains(code), $"{name}: expected {category}, got {code}: {actual!.Message}");
        if (Str(expected["message"]) is { } message)
        {
            Assert.True(MessageMatches(message, actual!.Message), $"{name}: expected message containing '{message}', got '{actual.Message}'");
        }
    }

    /// <summary>
    /// Whether an emitted error message matches the expected fragment, reconciling wording
    /// differences between SDKs the same way the reference harness does.
    /// </summary>
    public static bool MessageMatches(string expected, string actual)
    {
        static string Norm(string m) => m.ToLowerInvariant().Replace('"', '\'').Replace("','", "', '", StringComparison.Ordinal);
        var e = Norm(expected);
        var a = Norm(actual);
        return a.Contains(e, StringComparison.Ordinal)
               || e.Contains("orphan", StringComparison.Ordinal) && (a.Contains("orphan", StringComparison.Ordinal) || a.Contains("not reachable", StringComparison.Ordinal))
               || e.Contains("circular", StringComparison.Ordinal) && (a.Contains("circular", StringComparison.Ordinal) || a.Contains("self-reference", StringComparison.Ordinal))
               || e.Contains("cannot contain child", StringComparison.Ordinal) && a.Contains("cannot contain child", StringComparison.Ordinal)
               || e.Contains("cannot be placed under parent", StringComparison.Ordinal) && a.Contains("cannot be placed under parent", StringComparison.Ordinal)
               || e.Contains("protocol version mismatch", StringComparison.Ordinal) && a.Contains("protocol version mismatch", StringComparison.Ordinal);
    }

    public static void Json(JsonNode? expected, JsonNode? actual, string because)
    {
        if (expected is JsonValue ev && actual is JsonValue av
            && ev.GetValueKind() == JsonValueKind.Number && av.GetValueKind() == JsonValueKind.Number)
        {
            Assert.True(A2UIValues.ToDouble(ev) == A2UIValues.ToDouble(av), $"{because}: expected {ev}, got {av}");
            return;
        }

        Assert.True(
            JsonNode.DeepEquals(Normalize(expected), Normalize(actual)),
            $"{because}: expected {expected?.ToJsonString() ?? "null"}, got {actual?.ToJsonString() ?? "null"}");
    }

    /// <summary>Like <see cref="Json"/>, but a number also matches its string form (as in the reference harness).</summary>
    public static void PropertyValue(JsonNode? expected, JsonNode? actual, string because)
    {
        if (expected is JsonValue ev && ev.GetValueKind() == JsonValueKind.String
            && actual is JsonValue av && av.GetValueKind() == JsonValueKind.Number
            && av.ToJsonString() == ev.GetValue<string>())
        {
            return;
        }

        Json(expected, actual, because);
    }

    /// <summary>Checks <c>isValid</c>, <c>validationErrors</c>, <c>validationResults</c> and <c>validationResult</c> expectations.</summary>
    public static void ValidationFields(
        JsonObject expectedSurface,
        Surface surface,
        IReadOnlyDictionary<string, ProcessingConformanceTests.RenderedNode> rendered,
        string name)
    {
        foreach (var check in new[] { "isValid", "validationErrors", "validationResults", "validationResult" })
        {
            if (expectedSurface[check] is not JsonObject perComponent)
            {
                continue;
            }

            foreach (var (key, expectedValue) in perComponent)
            {
                Assert.True(rendered.TryGetValue(key, out var r), $"{name}: component instance '{key}' was not rendered");
                var outcome = surface.EvaluateChecks(r!.Component, r.Context);
                switch (check)
                {
                    case "isValid":
                        Assert.Equal(expectedValue!.GetValue<bool>(), outcome.IsValid);
                        break;
                    case "validationErrors":
                        Json(expectedValue, new JsonArray(outcome.ErrorMessages.Select(m => (JsonNode)m).ToArray()), $"{name}: {key} errors");
                        break;
                    case "validationResults":
                        Json(expectedValue, new JsonArray(outcome.Failures.Select(f => (JsonNode)f.DeepClone()).ToArray()), $"{name}: {key} results");
                        break;
                    case "validationResult":
                        var actual = outcome.Failures.FirstOrDefault() ?? new JsonObject { ["valid"] = true };
                        foreach (var (k, v) in expectedValue!.AsObject())
                        {
                            Json(v, actual[k], $"{name}: {key} result.{k}");
                        }

                        break;
                }
            }
        }
    }

    private static JsonNode? Normalize(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => JsonValue.Create(A2UIValues.ToDouble(v)!.Value),
        JsonValue v when v.GetValueKind() == JsonValueKind.Null => null,
        JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, Normalize(p.Value)))),
        JsonArray a => new JsonArray(a.Select(Normalize).ToArray()),
        _ => node.DeepClone(),
    };

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
