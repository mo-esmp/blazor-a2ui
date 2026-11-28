using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using BlazorA2UI.Model;
using BlazorA2UI.Rendering;

namespace BlazorA2UI.Tests.Rendering;

/// <summary>
/// Tests for <see cref="A2UIComponentRegistry"/>.
/// </summary>
public sealed class A2UIComponentRegistryTests
{
    /// <summary>
    /// The id of a catalog other than the basic one.
    /// </summary>
    private const string OtherCatalogId = "https://example.test/catalog.json";

    /// <summary>
    /// Proves that a registered renderer keeps every member in a trimmed app, because Blazor sets its parameters by
    /// reflection and the registry is the only place that names the type.
    /// </summary>
    [Fact]
    public void Register_TypeParameter_KeepsEveryMemberWhenTrimmed()
    {
        // Arrange
        MethodInfo register = typeof(A2UIComponentRegistry).GetMethod(nameof(A2UIComponentRegistry.Register)).ShouldNotBeNull();

        // Act
        DynamicallyAccessedMembersAttribute? kept = register.GetGenericArguments()[0].GetCustomAttribute<DynamicallyAccessedMembersAttribute>();

        // Assert
        kept.ShouldNotBeNull().MemberTypes.ShouldBe(DynamicallyAccessedMemberTypes.All);
    }

    /// <summary>
    /// Proves that a renderer registered for the component's catalog wins over the one for every catalog.
    /// </summary>
    [Fact]
    public void TryGetRenderer_CatalogRenderer_WinsOverAnyCatalog()
    {
        // Arrange
        A2UIComponentRegistry registry = new();
        registry.Register<A2UIButton>("Text", BasicCatalog.CatalogId);

        // Act
        bool found = registry.TryGetRenderer(TextComponent(), out Type? renderer);

        // Assert
        found.ShouldBeTrue();
        renderer.ShouldBe(typeof(A2UIButton));
    }

    /// <summary>
    /// Proves that a component whose catalog has renderers, but none for its type, falls back to the renderer for
    /// every catalog.
    /// </summary>
    [Fact]
    public void TryGetRenderer_CatalogWithoutType_FallsBackToAnyCatalog()
    {
        // Arrange
        A2UIComponentRegistry registry = new();
        registry.Register<A2UIButton>("Image", BasicCatalog.CatalogId);
        registry.Register<A2UIButton>("Text", OtherCatalogId);

        // Act
        bool found = registry.TryGetRenderer(TextComponent(), out Type? renderer);

        // Assert
        found.ShouldBeTrue();
        renderer.ShouldBe(typeof(A2UIText));
    }

    /// <summary>
    /// Proves that a component without a renderer is not found, so it renders as a placeholder.
    /// </summary>
    [Fact]
    public void TryGetRenderer_Unregistered_ReturnsFalse()
    {
        // Arrange
        A2UIComponentRegistry registry = new();
        registry.Unregister("Text").ShouldBeTrue();

        // Act
        bool found = registry.TryGetRenderer(TextComponent(), out Type? renderer);

        // Assert
        found.ShouldBeFalse();
        renderer.ShouldBeNull();
    }

    /// <summary>
    /// Builds a basic catalog <c>Text</c> component.
    /// </summary>
    /// <returns>The component.</returns>
    private static ComponentInstance TextComponent()
    {
        MessageProcessor processor = new([BasicCatalog.Create()]);
        processor.ProcessOrThrow(JsonNode.Parse($$$"""{"version":"1.0","createSurface":{"surfaceId":"s","catalogId":"{{{BasicCatalog.CatalogId}}}","components":[{"id":"root","component":"Text","text":"Hi"}]}}"""));
        return processor.Surfaces["s"].Root.ShouldNotBeNull();
    }
}
