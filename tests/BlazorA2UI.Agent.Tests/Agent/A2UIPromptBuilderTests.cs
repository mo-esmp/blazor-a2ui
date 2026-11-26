using System.Text.RegularExpressions;
using BlazorA2UI.Catalogs;
using BlazorA2UI.Processing;

namespace BlazorA2UI.Tests.Agent;

/// <summary>
/// Tests for <see cref="A2UIPromptBuilder"/>.
/// </summary>
public sealed partial class A2UIPromptBuilderTests
{
    /// <summary>
    /// Proves that the prompt has the rules, the schema section with every part, and the end of the section.
    /// </summary>
    [Fact]
    public void Build_BasicCatalog_WritesRulesAndSchemaSection()
    {
        // Arrange
        A2UIPromptBuilder builder = new([BasicCatalog.Create()]);

        // Act
        string prompt = builder.Build();

        // Assert
        prompt.ShouldStartWith(builder.Rules);
        prompt.ShouldContain(A2UIResponseParser.OpenTag);
        prompt.ShouldContain(A2UIResponseParser.CloseTag);
        prompt.ShouldContain(A2UIPromptBuilder.SchemaBlockStart);
        prompt.ShouldContain(A2UIPromptBuilder.SchemaBlockEnd);
        Section(prompt, "Agent to Renderer Schema").ShouldNotBeNull();
        Section(prompt, "Common Types Schema").ShouldNotBeNull();
        JsonObject catalog = Section(prompt, "Catalog Schema").ShouldNotBeNull();
        catalog["catalogId"]!.GetValue<string>().ShouldBe(BasicCatalog.CatalogId);
        catalog["protocolVersion"]!.GetValue<string>().ShouldBe(A2UIProtocol.Version);
        catalog["components"]!.AsObject().ContainsKey("Button").ShouldBeTrue();
    }

    /// <summary>
    /// Proves that by default the protocol schema keeps only the four UI messages and the definitions they use.
    /// </summary>
    [Fact]
    public void Build_DefaultMessages_SchemaKeepsOnlyUiMessages()
    {
        // Arrange
        A2UIPromptBuilder builder = new([BasicCatalog.Create()]);

        // Act
        JsonObject schema = Section(builder.Build(), "Agent to Renderer Schema").ShouldNotBeNull();

        // Assert
        schema["oneOf"]!.AsArray().Count.ShouldBe(4);
        JsonObject defs = schema["$defs"]!.AsObject();
        defs.ContainsKey("CreateSurfaceMessage").ShouldBeTrue();
        defs.ContainsKey("DeleteSurfaceMessage").ShouldBeTrue();
        defs.ContainsKey("CallRendererFunctionMessage").ShouldBeFalse();
        defs.ContainsKey("AgentFunctionResponseMessage").ShouldBeFalse();
        builder.Rules.ShouldContain("createSurface, updateComponents, updateDataModel, deleteSurface.");
        builder.Rules.ShouldNotContain("callRendererFunction");
    }

    /// <summary>
    /// Proves that allowing one message keeps only that message in the schema and the rules.
    /// </summary>
    [Fact]
    public void Build_OneMessage_SchemaKeepsOnlyThatMessage()
    {
        // Arrange
        A2UIPromptBuilder builder = new([BasicCatalog.Create()]) { AllowedMessages = A2UIAgentMessages.CallRendererFunction };

        // Act
        JsonObject schema = Section(builder.Build(), "Agent to Renderer Schema").ShouldNotBeNull();

        // Assert
        schema["oneOf"]!.AsArray().Count.ShouldBe(1);
        schema["$defs"]!.AsObject().Select(p => p.Key).ShouldBe(["CallRendererFunctionMessage"]);
        builder.Rules.ShouldContain("Use only these messages: callRendererFunction.");
    }

    /// <summary>
    /// Proves that every message is in the schema when all are allowed.
    /// </summary>
    [Fact]
    public void Build_AllMessages_SchemaHasEveryMessage()
    {
        // Arrange
        A2UIPromptBuilder builder = new([BasicCatalog.Create()]) { AllowedMessages = A2UIAgentMessages.All };

        // Act
        JsonObject schema = Section(builder.Build(), "Agent to Renderer Schema").ShouldNotBeNull();

        // Assert
        schema["oneOf"]!.AsArray().Count.ShouldBe(6);
    }

    /// <summary>
    /// Proves that a prompt with no allowed message is refused.
    /// </summary>
    [Fact]
    public void Build_NoAllowedMessages_Throws()
    {
        // Arrange
        A2UIPromptBuilder builder = new([BasicCatalog.Create()]) { AllowedMessages = A2UIAgentMessages.None };

        // Act
        Action act = () => builder.Build();

        // Assert
        act.ShouldThrow<InvalidOperationException>();
    }

    /// <summary>
    /// Proves that every common type the prompt refers to is defined in its common types section, and that the
    /// section holds nothing else.
    /// </summary>
    [Fact]
    public void Build_BasicCatalog_CommonTypesHoldExactlyWhatIsUsed()
    {
        // Arrange
        A2UIPromptBuilder builder = new([BasicCatalog.Create()]);

        // Act
        string prompt = builder.Build();

        // Assert
        JsonObject commonTypes = Section(prompt, "Common Types Schema").ShouldNotBeNull();
        HashSet<string> defined = [.. commonTypes["$defs"]!.AsObject().Select(p => p.Key)];
        HashSet<string> referenced = [.. CommonRef().Matches(prompt).Select(m => m.Groups[1].Value)];
        referenced.ShouldBeSubsetOf(defined);
        defined.ShouldNotBeEmpty();
        foreach (string name in defined)
        {
            bool used = referenced.Contains(name) || prompt.Contains($"\"#/$defs/{name}\"", StringComparison.Ordinal);
            used.ShouldBeTrue($"'{name}' is in the common types but nothing refers to it.");
        }
    }

    /// <summary>
    /// Proves that functions only the agent may call are left out, and functions declared in code are described.
    /// </summary>
    [Fact]
    public void Build_CodeFunctions_DropsAgentOnlyAndDescribesTheRest()
    {
        // Arrange
        JsonObject args = new() { ["type"] = "object", ["properties"] = new JsonObject { ["key"] = new JsonObject { ["type"] = "string" } } };
        Catalog catalog = BasicCatalog.Create().Extend(functions:
        [
            new FunctionDefinition("lookup") { AllowedCallers = AllowedCallers.RendererOrAgent, ArgsSchema = args, Description = "Looks a key up.", ReturnType = "string" },
            new FunctionDefinition("notify") { AllowedCallers = AllowedCallers.AgentOnly },
        ]);
        A2UIPromptBuilder builder = new([catalog]);

        // Act
        JsonObject document = Section(builder.Build(), "Catalog Schema").ShouldNotBeNull();

        // Assert
        JsonObject functions = document["functions"]!.AsObject();
        functions.ContainsKey("notify").ShouldBeFalse();
        functions.ContainsKey("formatString").ShouldBeTrue();
        JsonObject lookup = functions["lookup"]!.AsObject();
        lookup["description"]!.GetValue<string>().ShouldBe("Looks a key up.");
        lookup["allowedCallers"]!.GetValue<string>().ShouldBe("rendererOrAgent");
        lookup["returnType"]!.GetValue<string>().ShouldBe("string");
        lookup["properties"]!["@call"]!["const"]!.GetValue<string>().ShouldBe("lookup");
        JsonNode.DeepEquals(lookup["properties"]!["args"], args).ShouldBeTrue();
    }

    /// <summary>
    /// Proves that the catalog's own document is not changed by building the prompt.
    /// </summary>
    [Fact]
    public void Build_AgentOnlyFunction_LeavesCatalogDocumentUnchanged()
    {
        // Arrange
        JsonObject source = BasicCatalog.GetDocument();
        source["functions"]!["notify"] = new JsonObject { ["type"] = "object", ["allowedCallers"] = "agentOnly" };
        Catalog catalog = Catalog.FromJson(source);
        JsonObject document = catalog.Document!; // Set: the catalog was read from a document.
        string before = document.ToJsonString();
        A2UIPromptBuilder builder = new([catalog]);

        // Act
        string prompt = builder.Build();

        // Assert
        Section(prompt, "Catalog Schema")!["functions"]!.AsObject().ContainsKey("notify").ShouldBeFalse();
        document.ToJsonString().ShouldBe(before);
    }

    /// <summary>
    /// Proves that a component the catalog's document does not describe is refused, since the model could not learn it.
    /// </summary>
    [Fact]
    public void Build_UndescribedComponent_Throws()
    {
        // Arrange
        Catalog catalog = BasicCatalog.Create().Extend(components: [new ComponentDefinition("Chart")]);
        A2UIPromptBuilder builder = new([catalog]);

        // Act
        Action act = () => builder.Build();

        // Assert
        act.ShouldThrow<InvalidOperationException>().Message.ShouldContain("Chart");
    }

    /// <summary>
    /// Proves that each catalog's instructions follow the schema section, and a catalog without any is skipped.
    /// </summary>
    [Fact]
    public void Build_CatalogInstructions_FollowSchemaSection()
    {
        // Arrange
        Catalog tools = new("urn:tools", [], [], instructions: "  Use lookup for prices.  \n");
        Catalog plain = new("urn:plain", [], []);
        A2UIPromptBuilder builder = new([plain, tools]);

        // Act
        string prompt = builder.Build();

        // Assert
        int end = prompt.IndexOf(A2UIPromptBuilder.SchemaBlockEnd, StringComparison.Ordinal);
        prompt.IndexOf("### Instructions for catalog urn:tools:\nUse lookup for prices.\n", StringComparison.Ordinal).ShouldBeGreaterThan(end);
        prompt.ShouldNotContain("### Instructions for catalog urn:plain");
    }

    /// <summary>
    /// Proves that an example is written as a block that the parser reads back into the same messages.
    /// </summary>
    [Fact]
    public void Build_Example_WritesBlockTheParserReads()
    {
        // Arrange
        JsonObject create = A2UIMessages.CreateSurface("main", new Text { Content = "Hello" });
        JsonObject update = A2UIMessages.UpdateDataModel("main", "/name", "Ada");
        A2UIPromptBuilder builder = new([BasicCatalog.Create()]);
        builder.Examples.Add([create, update]);

        // Act
        string prompt = builder.Build();

        // Assert
        string examples = prompt[prompt.IndexOf("### Examples:", StringComparison.Ordinal)..];
        A2UIMessagePart[] messages = [.. A2UIResponseParser.Parse(examples).OfType<A2UIMessagePart>()];
        messages.Length.ShouldBe(2);
        JsonNode.DeepEquals(messages[0].Message, create).ShouldBeTrue();
        JsonNode.DeepEquals(messages[1].Message, update).ShouldBeTrue();
    }

    /// <summary>
    /// Proves that an example a renderer would reject is refused when the prompt is built.
    /// </summary>
    [Fact]
    public void Build_InvalidExample_Throws()
    {
        // Arrange
        JsonObject delete = JsonNode.Parse("""{"version":"1.0","deleteSurface":{"surfaceId":5}}""")!.AsObject();
        A2UIPromptBuilder builder = new([BasicCatalog.Create()]);
        builder.Examples.Add([delete]);

        // Act
        Action act = () => builder.Build();

        // Assert
        act.ShouldThrow<InvalidOperationException>().Message.ShouldStartWith("Example 1 would be rejected by the renderer");
    }

    /// <summary>
    /// Proves that an example with a message the prompt does not allow is refused.
    /// </summary>
    [Fact]
    public void Build_ExampleWithDisallowedMessage_Throws()
    {
        // Arrange
        A2UIPromptBuilder builder = new([BasicCatalog.Create()]) { AllowedMessages = A2UIAgentMessages.CreateSurface };
        builder.Examples.Add([A2UIMessages.DeleteSurface("main")]);

        // Act
        Action act = () => builder.Build();

        // Assert
        act.ShouldThrow<InvalidOperationException>().Message.ShouldContain("'deleteSurface'");
    }

    /// <summary>
    /// Proves that a builder with no catalog is refused.
    /// </summary>
    [Fact]
    public void Constructor_NoCatalog_Throws()
    {
        // Arrange
        Catalog[] catalogs = [];

        // Act
        Action act = () => _ = new A2UIPromptBuilder(catalogs);

        // Assert
        act.ShouldThrow<ArgumentException>();
    }

    /// <summary>
    /// Proves that two catalogs with the same id are refused.
    /// </summary>
    [Fact]
    public void Constructor_DuplicateCatalogIds_Throws()
    {
        // Arrange
        Catalog[] catalogs = [new Catalog("urn:a", [], []), new Catalog("urn:a", [], [])];

        // Act
        Action act = () => _ = new A2UIPromptBuilder(catalogs);

        // Assert
        act.ShouldThrow<ArgumentException>().Message.ShouldContain("urn:a");
    }

    /// <summary>
    /// Proves that a catalog for another protocol version is refused.
    /// </summary>
    [Fact]
    public void Constructor_OtherProtocolVersion_Throws()
    {
        // Arrange
        Catalog[] catalogs = [new Catalog("urn:a", [], [], protocolVersion: "0.9")];

        // Act
        Action act = () => _ = new A2UIPromptBuilder(catalogs);

        // Assert
        act.ShouldThrow<ArgumentException>().Message.ShouldContain("0.9");
    }

    /// <summary>
    /// Returns the JSON of a schema section of the prompt, or null when the prompt has no such section.
    /// </summary>
    /// <param name="prompt">The prompt.</param>
    /// <param name="title">The section's title, without <c>###</c> and the colon.</param>
    private static JsonObject? Section(string prompt, string title)
    {
        string heading = $"### {title}:\n";
        int start = prompt.IndexOf(heading, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += heading.Length;
        int end = prompt.IndexOf('\n', start);
        return JsonNode.Parse(prompt[start..end])!.AsObject();
    }

    /// <summary>
    /// Matches a <c>$ref</c> to a common type and captures the type's name.
    /// </summary>
    [GeneratedRegex("\"\\$ref\":\"common_types\\.json#/\\$defs/([A-Za-z0-9_]+)\"")]
    private static partial Regex CommonRef();
}
