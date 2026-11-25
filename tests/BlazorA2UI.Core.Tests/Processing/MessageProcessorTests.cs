using System.Text.Json.Nodes;
using BlazorA2UI.Catalogs;
using BlazorA2UI.Processing;
using Shouldly;
using Xunit;

namespace BlazorA2UI.Tests.Processing;

/// <summary>
/// Tests for <see cref="MessageProcessor"/> behavior that the conformance suites do not cover.
/// </summary>
public sealed class MessageProcessorTests
{
    /// <summary>
    /// The id of the basic catalog the tests create surfaces with.
    /// </summary>
    private static readonly string s_basicId = BasicCatalog.CatalogId;

    /// <summary>
    /// Proves that a <c>createSurface</c> rejected while applying its components leaves no surface behind, so the id can be used again.
    /// </summary>
    [Fact]
    public void ProcessOrThrow_RejectedCreateSurface_PublishesNoSurface()
    {
        // Arrange
        MessageProcessor processor = new([BasicCatalog.Create()]);
        JsonNode? bad = Message($$$"""{"surfaceId":"s","catalogId":"{{{s_basicId}}}","components":[]}""");

        // Act
        Action process = () => processor.ProcessOrThrow(bad, A2UIValidationConfig.Strict);

        // Assert
        process.ShouldThrow<A2UIException>();
        processor.Surfaces.ShouldNotContainKey("s");
        processor.ProcessOrThrow(Message($$$"""{"surfaceId":"s","catalogId":"{{{s_basicId}}}"}"""));
        processor.Surfaces.ShouldContainKey("s");
    }

    /// <summary>
    /// Proves that a function call on a surface cannot use a catalog registered after the surface was created.
    /// </summary>
    [Fact]
    public void Evaluate_CatalogAddedAfterCreateSurface_ReportsCatalogError()
    {
        // Arrange
        MessageProcessor processor = new([BasicCatalog.Create()]);
        processor.ProcessOrThrow(Message($$$"""{"surfaceId":"s","catalogId":"{{{s_basicId}}}"}"""));
        processor.AddCatalog(BasicCatalog.Create().WithCatalogId("test:later"));
        Surface surface = processor.Surfaces["s"];
        JsonNode? call = JsonNode.Parse("""{"@call":"not","catalogId":"test:later","args":{"value":true}}""");

        // Act
        surface.Evaluate(call, surface.RootContext, out _, out IReadOnlyList<A2UIException> errors);

        // Assert
        errors.ShouldContain(e => e.Code == A2UIErrorCodes.CatalogError);
    }

    /// <summary>
    /// Proves that strict validation reports <c>@accessibility</c> values that break the accessibility schema.
    /// </summary>
    /// <param name="accessibility">The <c>@accessibility</c> object, as JSON.</param>
    [Theory]
    [InlineData("""{"live":"loud"}""")]
    [InlineData("""{"hidden":"yes"}""")]
    [InlineData("""{"label":42}""")]
    public void ProcessOrThrow_InvalidAccessibilityUnderStrictValidation_ReportsError(string accessibility)
    {
        // Arrange
        MessageProcessor processor = new([BasicCatalog.Create()]);
        List<A2UIErrorReport> errors = [];
        processor.ErrorReported += errors.Add;
        JsonNode? message = Message($$$"""
            {"surfaceId":"s","catalogId":"{{{s_basicId}}}",
             "components":[{"id":"root","component":"Text","text":"hi","@accessibility":{{{accessibility}}} } ] }
            """);

        // Act
        processor.ProcessOrThrow(message, A2UIValidationConfig.Strict);

        // Assert
        errors.ShouldContain(e => e.Message.Contains("@accessibility", StringComparison.Ordinal));
    }

    /// <summary>
    /// Proves that valid <c>@accessibility</c> values, including a bound <c>hidden</c>, pass strict validation.
    /// </summary>
    [Fact]
    public void ProcessOrThrow_ValidAccessibilityUnderStrictValidation_ReportsNothing()
    {
        // Arrange
        MessageProcessor processor = new([BasicCatalog.Create()]);
        List<A2UIErrorReport> errors = [];
        processor.ErrorReported += errors.Add;
        JsonNode? message = Message($$$"""
            {"surfaceId":"s","catalogId":"{{{s_basicId}}}",
             "components":[{"id":"root","component":"Text","text":"hi",
               "@accessibility":{"label":"Greeting","live":"polite","hidden":{"@path":"/hide"} } } ] }
            """);

        // Act
        processor.ProcessOrThrow(message, A2UIValidationConfig.Strict);

        // Assert
        errors.ShouldBeEmpty();
    }

    /// <summary>
    /// Wraps a <c>createSurface</c> body in a v1.0 envelope.
    /// </summary>
    /// <param name="createSurface">The <c>createSurface</c> body, as JSON.</param>
    private static JsonNode? Message(string createSurface) =>
        JsonNode.Parse($$$"""{"version":"1.0","createSurface":{{{createSurface}}} }""");
}
