namespace BlazorA2UI.Tests.Agent;

/// <summary>
/// Tests for <see cref="A2UIMessages"/> and the way it flattens component trees.
/// </summary>
public sealed class A2UIMessagesTests
{
    /// <summary>
    /// Proves that a tree flattens with the root id, generated ids under the nearest chosen id, and children before
    /// their parents.
    /// </summary>
    [Fact]
    public void CreateSurface_Tree_FlattensWithGeneratedIds()
    {
        // Arrange
        Column root = new()
        {
            Children =
            [
                new Text { Content = "Title" },
                new Card { Id = "details", Child = new Text { Content = A2UIValue.Path("/details") } },
            ],
        };

        // Act
        JsonObject message = A2UIMessages.CreateSurface("main", root, new JsonObject { ["details"] = "x" });

        // Assert
        message.ToJsonString().ShouldBe(
            "{\"version\":\"1.0\",\"createSurface\":{\"surfaceId\":\"main\"," +
            "\"catalogId\":\"https://a2ui.org/specification/v1_0/catalogs/basic/catalog.json\",\"dataModel\":{\"details\":\"x\"}," +
            "\"components\":[" +
            "{\"id\":\"root__text_1\",\"component\":\"Text\",\"text\":\"Title\"}," +
            "{\"id\":\"details__text_1\",\"component\":\"Text\",\"text\":{\"@path\":\"/details\"}}," +
            "{\"id\":\"details\",\"component\":\"Card\",\"child\":\"details__text_1\"}," +
            "{\"id\":\"root\",\"component\":\"Column\",\"children\":[\"root__text_1\",\"details\"]}]}}");
    }

    /// <summary>
    /// Proves that a chosen id may not use the separator that generated ids use, so chosen and generated ids never
    /// collide.
    /// </summary>
    [Theory]
    [InlineData("card__text_1")]
    [InlineData("a__b")]
    public void UpdateComponents_ChosenIdWithSeparator_Throws(string id)
    {
        // Arrange
        Row root = new() { Children = [new Text { Content = "a" }, new Text { Id = id, Content = "b" }] };

        // Act
        Action build = () => A2UIMessages.UpdateComponents("main", root);

        // Assert
        build.ShouldThrow<InvalidOperationException>();
    }

    /// <summary>
    /// Proves that updating one part of a surface leaves a sibling with a chosen id alone: the renderer still shows the
    /// sibling's text after the update.
    /// </summary>
    [Fact]
    public void UpdateComponents_PartialUpdateNextToChosenSibling_KeepsSibling()
    {
        // Arrange
        using A2UIMessageValidator validator = new();
        static Card BuildCard() => new() { Id = "card", Child = new Text { Content = "inside" } };
        Column root = new() { Children = [BuildCard(), new Text { Id = "card_text_1", Content = "sibling" }] };
        validator.Validate(A2UIMessages.CreateSurface("main", root)).IsValid.ShouldBeTrue();

        // Act
        A2UIValidationResult result = validator.Validate(A2UIMessages.UpdateComponents("main", [BuildCard()]));

        // Assert
        result.IsValid.ShouldBeTrue();
        validator.Surfaces["main"].Components["card_text_1"].Properties["text"]!.GetValue<string>().ShouldBe("sibling");
        validator.Surfaces["main"].Components["card__text_1"].Properties["text"]!.GetValue<string>().ShouldBe("inside");
    }

    /// <summary>
    /// Proves that a component used twice is written once and referenced by both parents.
    /// </summary>
    [Fact]
    public void UpdateComponents_SharedComponent_WritesItOnce()
    {
        // Arrange
        Text shared = new() { Content = "same" };
        Column root = new() { Children = [shared, new Card { Child = shared }] };

        // Act
        JsonArray components = Components(A2UIMessages.UpdateComponents("main", root));

        // Assert
        Ids(components).ShouldBe(["root__text_1", "root__card_1", "root"]);
        components[1]!["child"]!.GetValue<string>().ShouldBe("root__text_1");
    }

    /// <summary>
    /// Proves that a reference places an existing component without sending it again.
    /// </summary>
    [Fact]
    public void UpdateComponents_Reference_IsReferencedNotWritten()
    {
        // Arrange
        Column root = new() { Children = [A2UIComponent.Ref("header"), new Divider()] };

        // Act
        JsonArray components = Components(A2UIMessages.UpdateComponents("main", root));

        // Assert
        Ids(components).ShouldBe(["root__divider_1", "root"]);
        components[1]!["children"]!.ToJsonString().ShouldBe("""["header","root__divider_1"]""");
    }

    /// <summary>
    /// Proves that a template list writes the template and its data path.
    /// </summary>
    [Fact]
    public void UpdateComponents_Template_WritesComponentIdAndPath()
    {
        // Arrange
        List root = new() { Children = A2UIChildList.Template(new Text { Id = "row", Content = A2UIValue.Path("name") }, "/people") };

        // Act
        JsonArray components = Components(A2UIMessages.UpdateComponents("main", root));

        // Assert
        components[1]!["children"]!.ToJsonString().ShouldBe("""{"componentId":"row","path":"/people"}""");
    }

    /// <summary>
    /// Proves that a partial update keeps each top-level id and generates the same ids each time, so a later update
    /// replaces the same components.
    /// </summary>
    [Fact]
    public void UpdateComponents_PartialTwice_GeneratesSameIds()
    {
        // Arrange
        static Card Build(string text) => new() { Id = "status", Child = new Text { Content = text } };

        // Act
        JsonArray first = Components(A2UIMessages.UpdateComponents("main", [Build("Pending")]));
        JsonArray second = Components(A2UIMessages.UpdateComponents("main", [Build("Done")]));

        // Assert
        Ids(first).ShouldBe(["status__text_1", "status"]);
        Ids(second).ShouldBe(Ids(first));
    }

    /// <summary>
    /// Proves that the shapes a renderer would reject are refused while the message is built.
    /// </summary>
    [Fact]
    public void UpdateComponents_InvalidTrees_Throw()
    {
        // Arrange
        Text a = new() { Id = "x", Content = "a" };
        Text b = new() { Id = "x", Content = "b" };
        A2UIChildList loop = [];
        Column cycle = new() { Children = loop };
        loop.Add(cycle);

        // Act
        Action duplicate = () => A2UIMessages.UpdateComponents("main", new Row { Children = [a, b] });
        Action selfContaining = () => A2UIMessages.UpdateComponents("main", cycle);
        Action wrongRootId = () => A2UIMessages.UpdateComponents("main", new Text { Id = "other", Content = "a" });
        Action partialWithoutId = () => A2UIMessages.UpdateComponents("main", [new Text { Content = "a" }]);
        Action referenceAsRoot = () => A2UIMessages.UpdateComponents("main", A2UIComponent.Ref("x"));

        // Assert
        duplicate.ShouldThrow<InvalidOperationException>();
        selfContaining.ShouldThrow<InvalidOperationException>();
        wrongRootId.ShouldThrow<ArgumentException>();
        partialWithoutId.ShouldThrow<ArgumentException>();
        referenceAsRoot.ShouldThrow<ArgumentException>();
    }

    /// <summary>
    /// Proves that a reference to a component the same message writes is refused, because it can close a cycle such
    /// as a root that references itself, while a reference to a component outside the message is kept.
    /// </summary>
    [Fact]
    public void UpdateComponents_ReferenceToOwnComponent_Throws()
    {
        // Arrange
        Column selfReferencing = new() { Children = [A2UIComponent.Ref("root")] };
        Column generatedTarget = new() { Children = [new Text { Content = "a" }, A2UIComponent.Ref("root__text_1")] };
        Card partial = new() { Id = "card", Child = A2UIComponent.Ref("header") };

        // Act
        Action toRoot = () => A2UIMessages.UpdateComponents("main", selfReferencing);
        Action toGenerated = () => A2UIMessages.UpdateComponents("main", generatedTarget);
        JsonArray components = Components(A2UIMessages.UpdateComponents("main", [partial]));

        // Assert
        toRoot.ShouldThrow<InvalidOperationException>();
        toGenerated.ShouldThrow<InvalidOperationException>();
        components.ShouldHaveSingleItem()!["child"]!.GetValue<string>().ShouldBe("header");
    }

    /// <summary>
    /// Proves that a tree deeper than the renderer accepts is refused.
    /// </summary>
    [Fact]
    public void UpdateComponents_TooDeep_Throws()
    {
        // Arrange
        A2UIComponent node = new Text { Content = "leaf" };
        for (int i = 0; i <= 51; i++)
        {
            node = new Card { Child = node };
        }

        // Act
        Action build = () => A2UIMessages.UpdateComponents("main", node);

        // Assert
        build.ShouldThrow<InvalidOperationException>();
    }

    /// <summary>
    /// Proves that the depth limit applies on every path to a shared component: a component first reached near the
    /// top is refused when a longer path to it would put its descendants past the limit.
    /// </summary>
    [Fact]
    public void UpdateComponents_SharedSubtreeTooDeepOnLongerPath_Throws()
    {
        // Arrange
        A2UIComponent shared = new Text { Content = "leaf" };
        for (int i = 0; i < 25; i++)
        {
            shared = new Card { Child = shared };
        }

        A2UIComponent longPath = shared;
        for (int i = 0; i < 30; i++)
        {
            longPath = new Card { Child = longPath };
        }

        Column root = new() { Children = [shared, longPath] };

        // Act
        Action build = () => A2UIMessages.UpdateComponents("main", root);

        // Assert
        build.ShouldThrow<InvalidOperationException>();
    }

    /// <summary>
    /// Proves that the data model and surface messages have the spec's shape, and that a null value deletes.
    /// </summary>
    [Fact]
    public void DataAndDeleteMessages_WriteSpecShape()
    {
        // Arrange
        // Act
        JsonObject set = A2UIMessages.UpdateDataModel("main", "/user/name", "Ada");
        JsonObject remove = A2UIMessages.UpdateDataModel("main", "/user/name", null);
        JsonObject replaceAll = A2UIMessages.UpdateDataModel("main", null, new JsonObject());
        JsonObject delete = A2UIMessages.DeleteSurface("main");

        // Assert
        set.ToJsonString().ShouldBe("""{"version":"1.0","updateDataModel":{"surfaceId":"main","path":"/user/name","value":"Ada"}}""");
        remove.ToJsonString().ShouldBe("""{"version":"1.0","updateDataModel":{"surfaceId":"main","path":"/user/name","value":null}}""");
        replaceAll.ToJsonString().ShouldBe("""{"version":"1.0","updateDataModel":{"surfaceId":"main","value":{}}}""");
        delete.ToJsonString().ShouldBe("""{"version":"1.0","deleteSurface":{"surfaceId":"main"}}""");
    }

    /// <summary>
    /// Proves that an icon needs exactly one of a name and an SVG path.
    /// </summary>
    [Fact]
    public void UpdateComponents_IconWithBothOrNeither_Throws()
    {
        // Arrange
        Icon both = new() { Name = IconNames.Add, SvgPath = "M0 0" };
        Icon neither = new();

        // Act
        Action withBoth = () => A2UIMessages.UpdateComponents("main", both);
        Action withNeither = () => A2UIMessages.UpdateComponents("main", neither);

        // Assert
        withBoth.ShouldThrow<InvalidOperationException>();
        withNeither.ShouldThrow<InvalidOperationException>();
    }

    /// <summary>
    /// Returns a message's component list.
    /// </summary>
    /// <param name="message">A <c>createSurface</c> or <c>updateComponents</c> message.</param>
    private static JsonArray Components(JsonObject message) =>
        (JsonArray)(message["updateComponents"] ?? message["createSurface"])!["components"]!;

    /// <summary>
    /// Returns the ids of a component list, in order.
    /// </summary>
    /// <param name="components">The list.</param>
    private static List<string> Ids(JsonArray components) => [.. components.Select(c => c!["id"]!.GetValue<string>())];
}
