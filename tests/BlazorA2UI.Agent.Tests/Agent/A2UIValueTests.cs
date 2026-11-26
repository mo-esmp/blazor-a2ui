namespace BlazorA2UI.Tests.Agent;

/// <summary>
/// Tests for <see cref="A2UIValue"/>, <see cref="A2UICall"/>, <see cref="A2UIAction"/> and <see cref="A2UICheck"/>.
/// </summary>
public sealed class A2UIValueTests
{
    /// <summary>
    /// Proves that strings, numbers and booleans convert to JSON literals.
    /// </summary>
    [Fact]
    public void ImplicitConversion_Primitives_WritesLiterals()
    {
        // Arrange
        A2UIValue text = "hi";
        A2UIValue number = 1.5;
        A2UIValue integer = 3;
        A2UIValue flag = true;

        // Act
        string json = new JsonArray(text.ToJson(), number.ToJson(), integer.ToJson(), flag.ToJson()).ToJsonString();

        // Assert
        json.ShouldBe("""["hi",1.5,3,true]""");
        text.Kind.ShouldBe(A2UIValueKind.Literal);
    }

    /// <summary>
    /// Proves that a binding writes <c>@path</c> and keeps a relative path as written.
    /// </summary>
    [Fact]
    public void Path_Relative_WritesPathAsWritten()
    {
        // Arrange
        A2UIValue binding = A2UIValue.Path("item/name");

        // Act
        string json = binding.ToJson()!.ToJsonString();

        // Assert
        json.ShouldBe("""{"@path":"item/name"}""");
        binding.Kind.ShouldBe(A2UIValueKind.Path);
    }

    /// <summary>
    /// Proves that a call writes <c>@call</c>, its arguments and its catalog, and leaves out empty arguments.
    /// </summary>
    [Fact]
    public void Call_WithArgsAndCatalog_WritesCall()
    {
        // Arrange
        A2UICall call = new("lookup") { CatalogId = "urn:tools" };
        call.Args["key"] = A2UIValue.Path("/k");

        // Act
        A2UIValue value = call;

        // Assert
        value.Kind.ShouldBe(A2UIValueKind.Call);
        value.ToJson()!.ToJsonString().ShouldBe("""{"@call":"lookup","args":{"key":{"@path":"/k"}},"catalogId":"urn:tools"}""");
        new A2UICall("now").ToJson().ToJsonString().ShouldBe("""{"@call":"now"}""");
    }

    /// <summary>
    /// Proves that a literal object with a reserved key is rejected, because the renderer would read it as a binding,
    /// and that an escaped <c>@@</c> key is allowed.
    /// </summary>
    [Fact]
    public void Literal_ReservedKey_Throws()
    {
        // Arrange
        JsonObject binding = new() { ["@path"] = "/secret" };
        JsonObject escaped = new() { ["@@handle"] = "x" };

        // Act
        Action reserved = () => A2UIValue.Literal(binding);

        // Assert
        reserved.ShouldThrow<ArgumentException>();
        A2UIValue.Literal(escaped).ToJson()!.ToJsonString().ShouldBe("""{"@@handle":"x"}""");
    }

    /// <summary>
    /// Proves that a reserved key is rejected at any depth of a literal, inside objects and arrays.
    /// </summary>
    [Theory]
    [InlineData("""{"nested":{"@path":"/secret"}}""")]
    [InlineData("""[1,{"@call":"openUrl"}]""")]
    [InlineData("""{"list":[{"ok":{"@":"x"}}]}""")]
    public void Literal_NestedReservedKey_Throws(string json)
    {
        // Arrange
        JsonNode? literal = JsonNode.Parse(json);

        // Act
        Action create = () => A2UIValue.Literal(literal);

        // Assert
        create.ShouldThrow<ArgumentException>();
    }

    /// <summary>
    /// Proves that a literal is copied, so later changes to the source do not reach the message.
    /// </summary>
    [Fact]
    public void Literal_SourceChangedLater_KeepsCopy()
    {
        // Arrange
        JsonObject source = new() { ["a"] = 1 };
        A2UIValue value = A2UIValue.Literal(source);

        // Act
        source["a"] = 2;

        // Assert
        value.ToJson()!.ToJsonString().ShouldBe("""{"a":1}""");
    }

    /// <summary>
    /// Proves that a number JSON cannot carry is rejected.
    /// </summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ImplicitConversion_NotFinite_Throws(double number)
    {
        // Arrange
        Func<A2UIValue> convert = () => number;

        // Act
        // Assert
        convert.ShouldThrow<ArgumentException>();
    }

    /// <summary>
    /// Proves that an event action writes its name, user message and context.
    /// </summary>
    [Fact]
    public void Event_WithContext_WritesEvent()
    {
        // Arrange
        Dictionary<string, A2UIValue> context = new() { ["orderId"] = "o-1", ["email"] = A2UIValue.Path("/email") };

        // Act
        A2UIAction action = A2UIAction.Event("submit", context, "Order placed");

        // Assert
        action.ToJson().ToJsonString().ShouldBe(
            """{"event":{"name":"submit","userMessage":"Order placed","context":{"orderId":"o-1","email":{"@path":"/email"}}}}""");
    }

    /// <summary>
    /// Proves that a function call action wraps the call.
    /// </summary>
    [Fact]
    public void FunctionCall_OpenUrl_WritesFunctionCall()
    {
        // Arrange
        A2UICall call = BasicFunctions.OpenUrl("https://example.com");

        // Act
        A2UIAction action = A2UIAction.FunctionCall(call);

        // Assert
        action.ToJson().ToJsonString().ShouldBe("""{"functionCall":{"@call":"openUrl","args":{"url":"https://example.com"}}}""");
    }

    /// <summary>
    /// Proves that a check needs a binding or a call as its condition.
    /// </summary>
    [Fact]
    public void Check_LiteralCondition_Throws()
    {
        // Arrange
        Func<A2UICheck> create = () => new A2UICheck(true);

        // Act
        A2UICheck check = new(BasicFunctions.Required(A2UIValue.Path("/name")), "Enter a name.");

        // Assert
        create.ShouldThrow<ArgumentException>();
        check.ToJson().ToJsonString().ShouldBe(
            """{"condition":{"@call":"required","args":{"value":{"@path":"/name"}}},"message":"Enter a name."}""");
    }

    /// <summary>
    /// Proves that the range checks need a bound and that the logical calls need two values, as the catalog requires.
    /// </summary>
    [Fact]
    public void BasicFunctions_MissingRequiredArgs_Throw()
    {
        // Arrange
        A2UIValue value = A2UIValue.Path("/v");

        // Act
        Action length = () => BasicFunctions.Length(value);
        Action numeric = () => BasicFunctions.Numeric(value);
        Action and = () => BasicFunctions.And(value);

        // Assert
        length.ShouldThrow<ArgumentException>();
        numeric.ShouldThrow<ArgumentException>();
        and.ShouldThrow<ArgumentException>();
        BasicFunctions.Or(value, false).ToJson().ToJsonString().ShouldBe("""{"@call":"or","args":{"values":[{"@path":"/v"},false]}}""");
    }
}
