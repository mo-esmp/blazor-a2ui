using System.Text;
using System.Text.Json.Nodes;
using BlazorA2UI.Catalogs;
using BlazorA2UI.Data;
using BlazorA2UI.Processing;
using BlazorA2UI.Text;
using Xunit;

namespace BlazorA2UI.Tests;

/// <summary>Regression tests for behavior the conformance suites do not cover.</summary>
public class CoreRegressionTests
{
    private static readonly string s_basicId = BasicCatalog.Create().CatalogId;

    [Fact]
    public void RejectedWriteLeavesTheModelUnchanged()
    {
        DataModel model = new(JsonNode.Parse("""{"name":"x"}"""));

        Assert.Throws<A2UIDataException>(() => model.Set("/newList/10001/value", 1));
        Assert.Throws<A2UIDataException>(() => model.Set("/name/c/d", 1));

        Assert.Equal("""{"name":"x"}""", model.Snapshot()!.ToJsonString());
    }

    [Fact]
    public void WriteStillCreatesMissingContainers()
    {
        DataModel model = new();
        model.Set("/items/1/name", "b");
        Assert.Equal("""{"items":[null,{"name":"b"}]}""", model.Snapshot()!.ToJsonString());
    }

    [Fact]
    public void FunctionCallsOnlySeeTheSurfacesCatalogs()
    {
        MessageProcessor processor = new([BasicCatalog.Create()]);
        processor.ProcessOrThrow(JsonNode.Parse($$$"""{"version":"1.0","createSurface":{"surfaceId":"s","catalogId":"{{{s_basicId}}}"}}"""));

        JsonObject later = BasicCatalog.GetDocument();
        later["catalogId"] = "test:later";
        processor.AddCatalog(Catalog.FromJson(later));

        Surface surface = processor.Surfaces["s"];
        JsonNode? call = JsonNode.Parse("""{"@call":"not","catalogId":"test:later","args":{"value":true}}""");
        surface.Evaluate(call, surface.RootContext, out _, out IReadOnlyList<A2UIException>? errors);

        Assert.Contains(errors, e => e.Code == A2UIErrorCodes.CatalogError);
    }

    [Fact]
    public void RejectedCreateSurfaceIsNotPublished()
    {
        MessageProcessor processor = new([BasicCatalog.Create()]);
        JsonNode? bad = JsonNode.Parse($$$"""
            {"version":"1.0","createSurface":{"surfaceId":"s","catalogId":"{{{s_basicId}}}",
             "components":[]}}
            """);

        Assert.ThrowsAny<A2UIException>(() => processor.ProcessOrThrow(bad, A2UIValidationConfig.Strict));
        Assert.False(processor.Surfaces.ContainsKey("s"));

        processor.ProcessOrThrow(JsonNode.Parse($$$"""{"version":"1.0","createSurface":{"surfaceId":"s","catalogId":"{{{s_basicId}}}"}}"""));
        Assert.True(processor.Surfaces.ContainsKey("s"));
    }

    [Fact]
    public void OneOfNeedsExactlyOneMatch()
    {
        JsonNode? schema = JsonNode.Parse("""{"oneOf":[{"type":"number"},{"type":"integer"}]}""");
        Assert.NotNull(SchemaCheck.Validate(5, schema, null));
        Assert.Null(SchemaCheck.Validate(5.5, schema, null));
        Assert.NotNull(SchemaCheck.Validate("x", schema, null));
    }

    [Theory]
    [InlineData("""{"live":"loud"}""")]
    [InlineData("""{"hidden":"yes"}""")]
    [InlineData("""{"label":42}""")]
    public void StrictValidationChecksAccessibilityValues(string accessibility)
    {
        MessageProcessor processor = new([BasicCatalog.Create()]);
        JsonNode? message = JsonNode.Parse($$$"""
            {"version":"1.0","createSurface":{"surfaceId":"s","catalogId":"{{{s_basicId}}}",
             "components":[{"id":"root","component":"Text","text":"hi","@accessibility":{{{accessibility}}}}]}}
            """);

        List<A2UIErrorReport> errors = new();
        processor.ErrorReported += errors.Add;
        processor.ProcessOrThrow(message, A2UIValidationConfig.Strict);

        Assert.Contains(errors, e => e.Message.Contains("@accessibility", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidAccessibilityPassesStrictValidation()
    {
        MessageProcessor processor = new([BasicCatalog.Create()]);
        List<A2UIErrorReport> errors = new();
        processor.ErrorReported += errors.Add;
        processor.ProcessOrThrow(
            JsonNode.Parse($$$"""
                {"version":"1.0","createSurface":{"surfaceId":"s","catalogId":"{{{s_basicId}}}",
                 "components":[{"id":"root","component":"Text","text":"hi",
                   "@accessibility":{"label":"Greeting","live":"polite","hidden":{"@path":"/hide"} } } ]} }
                """),
            A2UIValidationConfig.Strict);
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("℘x", true)]   // Other_ID_Start
    [InlineData("a·b", true)]  // Other_ID_Continue
    [InlineData("a·", true)]   // Other_ID_Continue
    [InlineData("ⸯ", false)]   // Lm, but Pattern_Syntax
    [InlineData("゛", false)]   // ID_Start, not XID_Start
    [InlineData("_x1", true)]
    [InlineData("1x", false)]
    public void IdentifiersUseExactXidTables(string value, bool expected) =>
        Assert.Equal(expected, Uax31.IsIdentifier(value));

    [Fact]
    public void SupplementaryLettersAreIdentifiers() =>
        Assert.True(Uax31.IsIdentifier(new Rune(0x1D400).ToString() + "x"));
}
