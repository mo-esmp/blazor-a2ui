using System.Text.Json;
using System.Text.Json.Nodes;
using BlazorA2UI.Evaluation;
using BlazorA2UI.Processing;
using Shouldly;
using Xunit;

namespace BlazorA2UI.Tests.Conformance;

/// <summary>
/// Assertions shared by the conformance harnesses.
/// </summary>
internal static class ConformanceAssert
{
    /// <summary>
    /// Checks an error against a case's <c>expectError</c> category and message fragment.
    /// </summary>
    public static void Error(JsonObject expected, Exception? actual, string name)
    {
        string? category = Str(expected["category"]);
        actual.ShouldNotBeNull($"{name}: expected a {category} error but none was raised");
        string? code = (actual as A2UIException)?.Code;
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
        accepted.Contains(code).ShouldBeTrue($"{name}: expected {category}, got {code}: {actual.Message}");
        if (Str(expected["message"]) is { } message)
        {
            MessageMatches(message, actual.Message).ShouldBeTrue($"{name}: expected message containing '{message}', got '{actual.Message}'");
        }
    }

    /// <summary>
    /// Whether an emitted error message matches the expected fragment, reconciling wording
    /// differences between SDKs the same way the reference harness does.
    /// </summary>
    public static bool MessageMatches(string expected, string actual)
    {
        static string Norm(string m) => m.ToLowerInvariant().Replace('"', '\'').Replace("','", "', '", StringComparison.Ordinal);
        string e = Norm(expected);
        string a = Norm(actual);
        return a.Contains(e, StringComparison.Ordinal)
               || e.Contains("orphan", StringComparison.Ordinal) && (a.Contains("orphan", StringComparison.Ordinal) || a.Contains("not reachable", StringComparison.Ordinal))
               || e.Contains("circular", StringComparison.Ordinal) && (a.Contains("circular", StringComparison.Ordinal) || a.Contains("self-reference", StringComparison.Ordinal))
               || e.Contains("cannot contain child", StringComparison.Ordinal) && a.Contains("cannot contain child", StringComparison.Ordinal)
               || e.Contains("cannot be placed under parent", StringComparison.Ordinal) && a.Contains("cannot be placed under parent", StringComparison.Ordinal)
               || e.Contains("protocol version mismatch", StringComparison.Ordinal) && a.Contains("protocol version mismatch", StringComparison.Ordinal);
    }

    /// <summary>
    /// Asserts that two JSON values are equal, comparing numbers by value and treating JSON null like a missing value.
    /// </summary>
    public static void Json(JsonNode? expected, JsonNode? actual, string because)
    {
        if (expected is JsonValue ev && actual is JsonValue av
            && ev.GetValueKind() == JsonValueKind.Number && av.GetValueKind() == JsonValueKind.Number)
        {
            (A2UIValues.ToDouble(ev) == A2UIValues.ToDouble(av)).ShouldBeTrue($"{because}: expected {ev}, got {av}");
            return;
        }

        JsonNode.DeepEquals(Normalize(expected), Normalize(actual)).ShouldBeTrue($"{because}: expected {expected?.ToJsonString() ?? "null"}, got {actual?.ToJsonString() ?? "null"}");
    }

    /// <summary>
    /// Like <see cref="Json"/>, but a number also matches its string form (as in the reference harness).
    /// </summary>
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

    /// <summary>
    /// Checks <c>isValid</c>, <c>validationErrors</c>, <c>validationResults</c> and <c>validationResult</c> expectations.
    /// </summary>
    public static void ValidationFields(
        JsonObject expectedSurface,
        Surface surface,
        IReadOnlyDictionary<string, ProcessingConformanceTests.RenderedNode> rendered,
        string name)
    {
        foreach (string check in new[] { "isValid", "validationErrors", "validationResults", "validationResult" })
        {
            if (expectedSurface[check] is not JsonObject perComponent)
            {
                continue;
            }

            foreach ((string key, JsonNode? expectedValue) in perComponent)
            {
                rendered.TryGetValue(key, out ProcessingConformanceTests.RenderedNode? r).ShouldBeTrue($"{name}: component instance '{key}' was not rendered");
                CheckOutcome outcome = surface.EvaluateChecks(r.Required().Component, r.Context);
                switch (check)
                {
                    case "isValid":
                        outcome.IsValid.ShouldBe(expectedValue.Required().GetValue<bool>());
                        break;
                    case "validationErrors":
                        Json(expectedValue, new JsonArray(outcome.ErrorMessages.Select(m => (JsonNode)m).ToArray()), $"{name}: {key} errors");
                        break;
                    case "validationResults":
                        Json(expectedValue, new JsonArray(outcome.Failures.Select(f => (JsonNode)f.DeepClone()).ToArray()), $"{name}: {key} results");
                        break;
                    case "validationResult":
                        JsonObject actual = outcome.Failures.FirstOrDefault() ?? new JsonObject { ["valid"] = true };
                        foreach ((string k, JsonNode? v) in expectedValue.Required().AsObject())
                        {
                            Json(v, actual[k], $"{name}: {key} result.{k}");
                        }

                        break;
                }
            }
        }
    }

    /// <summary>
    /// Normalizes a JSON value for comparison: numbers become doubles and JSON null becomes null.
    /// </summary>
    private static JsonNode? Normalize(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => JsonValue.Create(A2UIValues.ToDouble(v).Required()),
        JsonValue v when v.GetValueKind() == JsonValueKind.Null => null,
        JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, Normalize(p.Value)))),
        JsonArray a => new JsonArray(a.Select(Normalize).ToArray()),
        _ => node.DeepClone(),
    };

    /// <summary>
    /// Reads a string value, or null when the node is not a string.
    /// </summary>
    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out string? s) ? s : null;
}
