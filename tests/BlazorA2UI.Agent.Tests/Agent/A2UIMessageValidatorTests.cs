using BlazorA2UI.Catalogs;
using BlazorA2UI.Evaluation;
using BlazorA2UI.Processing;

namespace BlazorA2UI.Tests.Agent;

/// <summary>
/// Tests for <see cref="A2UIMessageValidator"/>, including that every basic catalog builder produces messages a
/// renderer accepts under strict validation.
/// </summary>
public sealed class A2UIMessageValidatorTests
{
    /// <summary>
    /// Proves that a surface using every basic catalog component, check and function passes the renderer's strict
    /// validation, so the builders match the catalog.
    /// </summary>
    [Fact]
    public void Validate_EveryBasicComponent_IsValidUnderStrictRules()
    {
        // Arrange
        using A2UIMessageValidator validator = new([BasicCatalog.Create()], A2UIValidationConfig.Strict);
        A2UIValue email = A2UIValue.Path("/form/email");
        Column root = new()
        {
            Justify = Justify.SpaceBetween,
            Align = Alignment.Center,
            Children =
            [
                new Text { Content = BasicFunctions.FormatString("Hello, ${/user/name}"), Variant = TextVariant.Caption },
                new Text { Content = BasicFunctions.Pluralize(A2UIValue.Path("/count"), "${/count} items", one: "One item") },
                new Text { Content = BasicFunctions.FormatCurrency(A2UIValue.Path("/total"), "EUR", decimals: 2) },
                new Text { Content = BasicFunctions.FormatNumber(A2UIValue.Path("/total"), grouping: true) },
                new Text { Content = BasicFunctions.FormatDate(A2UIValue.Path("/when"), "yyyy-MM-dd") },
                new Image { Url = "https://cdn.example.com/a.png", Description = "A", Fit = ImageFit.ScaleDown, Variant = ImageVariant.Avatar },
                new Icon { Name = IconNames.ShoppingCart, Accessibility = new A2UIAccessibility { Label = "Cart", Live = A2UILive.Polite } },
                new Icon { SvgPath = "M0 0L8 8" },
                new Video { Url = "https://cdn.example.com/a.mp4", PosterUrl = "https://cdn.example.com/a.png" },
                new AudioPlayer { Url = "https://cdn.example.com/a.mp3", Description = "Clip" },
                new Row { Children = [new Text { Content = "a", Weight = 1 }, new Divider { Axis = DividerAxis.Vertical }] },
                new List
                {
                    Direction = ListDirection.Horizontal,
                    Align = Alignment.Start,
                    Children = A2UIChildList.Template(
                        new Text { Id = "person", Content = BasicFunctions.FormatString("${@index(offset: 1)}. ${name}") },
                        "/people"),
                },
                new Tabs
                {
                    Items =
                    [
                        new Tab("One", new Text { Content = "first" }),
                        new Tab("Two", new Card { Child = new Text { Content = "second" } }),
                    ],
                },
                new Modal { Trigger = new Text { Content = "Open" }, Content = new Text { Content = "Inside" } },
                new TextField
                {
                    Label = "Email",
                    Value = email,
                    Placeholder = "you@example.com",
                    Variant = TextFieldVariant.ShortText,
                    Checks =
                    [
                        new A2UICheck(BasicFunctions.Required(email), "Required"),
                        new A2UICheck(BasicFunctions.Email(email)),
                        new A2UICheck(BasicFunctions.Length(email, max: 100)),
                        new A2UICheck(BasicFunctions.Regex(email, "^[^ ]+$")),
                    ],
                },
                new CheckBox { Label = "Agree", Value = A2UIValue.Path("/form/agree") },
                new ChoicePicker
                {
                    Label = "Size",
                    Variant = ChoicePickerVariant.MultipleSelection,
                    DisplayStyle = ChoiceDisplayStyle.Chips,
                    Filterable = true,
                    Options = [new ChoiceOption("Small", "s"), new ChoiceOption("Large", "l")],
                    Value = A2UIValue.Path("/form/size"),
                },
                new Slider
                {
                    Label = "Qty",
                    Min = 1,
                    Max = 10,
                    Steps = 9,
                    Value = A2UIValue.Path("/form/qty"),
                    Checks = [new A2UICheck(BasicFunctions.Numeric(A2UIValue.Path("/form/qty"), min: 1))],
                },
                new DateTimeInput
                {
                    Label = "When",
                    Value = A2UIValue.Path("/form/when"),
                    EnableDate = true,
                    EnableTime = true,
                    Min = "2026-01-01T00:00:00Z",
                    Max = "2027-01-01T00:00:00Z",
                },
                new Button
                {
                    Variant = ButtonVariant.Primary,
                    Child = new Text { Content = "Send" },
                    Action = A2UIAction.Event("submit", new Dictionary<string, A2UIValue> { ["email"] = email }),
                    Checks = [new A2UICheck(BasicFunctions.And(BasicFunctions.Not(A2UIValue.Path("/busy")), A2UIValue.Path("/form/agree")))],
                },
                new Button
                {
                    Variant = ButtonVariant.Borderless,
                    Child = new Text { Content = "Help" },
                    Action = A2UIAction.FunctionCall(BasicFunctions.OpenUrl("https://example.com/help")),
                },
            ],
        };

        // Act
        A2UIValidationResult result = validator.Validate(A2UIMessages.CreateSurface("main", root, new JsonObject { ["people"] = new JsonArray() }));

        // Assert
        result.Errors.ShouldBeEmpty();
        result.IsValid.ShouldBeTrue();
        validator.Surfaces["main"].Components.Count.ShouldBe(32);
    }

    /// <summary>
    /// Proves that a message is checked against the ones before it: updating a surface that was never created fails,
    /// and creating the same surface twice fails.
    /// </summary>
    [Fact]
    public void Validate_MessagesOutOfOrder_ReportsErrors()
    {
        // Arrange
        using A2UIMessageValidator validator = new();
        JsonObject create = A2UIMessages.CreateSurface("main", new Text { Content = "hi" });

        // Act
        A2UIValidationResult early = validator.Validate(A2UIMessages.UpdateDataModel("main", "/a", 1));
        A2UIValidationResult first = validator.Validate(create);
        A2UIValidationResult again = validator.Validate(create);
        A2UIValidationResult update = validator.Validate(A2UIMessages.UpdateDataModel("main", "/a", 1));

        // Assert
        early.IsValid.ShouldBeFalse();
        early.Errors[0].SurfaceId.ShouldBe("main");
        first.IsValid.ShouldBeTrue();
        again.Errors.ShouldHaveSingleItem().Code.ShouldBe(A2UIErrorCodes.IntegrityError);
        update.IsValid.ShouldBeTrue();
        validator.Surfaces["main"].DataModel.Snapshot()!.ToJsonString().ShouldBe("""{"a":1}""");
    }

    /// <summary>
    /// Proves that a component the renderer would skip is reported even though the message as a whole is applied.
    /// </summary>
    [Fact]
    public void Validate_UnknownComponentUnderStrictRules_ReportsError()
    {
        // Arrange
        using A2UIMessageValidator validator = new([BasicCatalog.Create()], A2UIValidationConfig.Strict);
        JsonObject message = A2UIMessages.CreateSurface("main", new Custom());

        // Act
        A2UIValidationResult result = validator.Validate(message);

        // Assert
        result.IsValid.ShouldBeFalse();
    }

    /// <summary>
    /// Proves that a valid <c>callRendererFunction</c> passes without its function running.
    /// </summary>
    [Fact]
    public void Validate_CallRendererFunction_ChecksWithoutRunning()
    {
        // Arrange
        int runs = 0;
        using A2UIMessageValidator validator = ToolsValidator(() => runs++);

        // Act
        A2UIValidationResult result = validator.Validate(RendererCall("""{"@call":"echo","catalogId":"urn:tools","args":{"value":"x"}}"""));

        // Assert
        result.Errors.ShouldBeEmpty();
        runs.ShouldBe(0);
    }

    /// <summary>
    /// Proves that a <c>callRendererFunction</c> the renderer would refuse is reported, without running anything:
    /// an unknown catalog or function, a renderer-only function, a function that needs user activation, arguments
    /// that do not match the schema, a renderer-only call nested in the arguments, and an unknown surface.
    /// </summary>
    [Theory]
    [InlineData("""{"@call":"echo","catalogId":"urn:missing","args":{"value":"x"}}""")]
    [InlineData("""{"@call":"missing","catalogId":"urn:tools"}""")]
    [InlineData("""{"@call":"formatString","catalogId":"https://a2ui.org/specification/v1_0/catalogs/basic/catalog.json","args":{"value":"x"}}""")]
    [InlineData("""{"@call":"gesture","catalogId":"urn:tools"}""")]
    [InlineData("""{"@call":"echo","catalogId":"urn:tools","args":{"value":5}}""")]
    [InlineData("""{"@call":"echo","catalogId":"urn:tools"}""")]
    [InlineData("""{"@call":"echo","catalogId":"urn:tools","args":{"value":{"@call":"secret","catalogId":"urn:tools"}}}""")]
    [InlineData("""{"@call":"echo","catalogId":"urn:tools","args":{"other":{"@path":"/x"}}}""")]
    [InlineData("""{"@call":"echo","catalogId":"urn:tools","args":{"value":5,"other":{"@path":"/x"}}}""")]
    public void Validate_CallRendererFunctionRefused_ReportsInvalidFunctionCall(string callFunction)
    {
        // Arrange
        int runs = 0;
        using A2UIMessageValidator validator = ToolsValidator(() => runs++);

        // Act
        A2UIValidationResult result = validator.Validate(RendererCall(callFunction));

        // Assert
        A2UIErrorReport error = result.Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe(A2UIErrorCodes.InvalidFunctionCall);
        error.FunctionCallId.ShouldBe("c1");
        runs.ShouldBe(0);
    }

    /// <summary>
    /// Proves that a system function call the renderer would refuse is reported, as the execution error its response
    /// would carry: an unknown system function, and <c>@index</c> outside a list, at the top level or nested in the
    /// arguments.
    /// </summary>
    [Theory]
    [InlineData("""{"@call":"@missing","catalogId":"urn:tools"}""")]
    [InlineData("""{"@call":"@index","catalogId":"urn:tools"}""")]
    [InlineData("""{"@call":"echo","catalogId":"urn:tools","args":{"value":{"@call":"@index"}}}""")]
    public void Validate_CallRendererFunctionSystemCallRefused_ReportsExecutionError(string callFunction)
    {
        // Arrange
        using A2UIMessageValidator validator = ToolsValidator(() => { });

        // Act
        A2UIValidationResult result = validator.Validate(RendererCall(callFunction));

        // Assert
        A2UIErrorReport error = result.Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe(A2UIErrorCodes.ExecutionError);
        error.FunctionCallId.ShouldBe("c1");
    }

    /// <summary>
    /// Proves that a binding in the arguments defers only the checks that depend on its value: a call whose required
    /// argument is a binding passes.
    /// </summary>
    [Fact]
    public void Validate_CallRendererFunctionBoundArgument_IsValid()
    {
        // Arrange
        using A2UIMessageValidator validator = ToolsValidator(() => { });

        // Act
        A2UIValidationResult result = validator.Validate(RendererCall("""{"@call":"echo","catalogId":"urn:tools","args":{"value":{"@path":"/x"}}}"""));

        // Assert
        result.Errors.ShouldBeEmpty();
    }

    /// <summary>
    /// Proves that a refused renderer function call does not stop a batch: the renderer answers the call with an
    /// error and applies the next message, and so does the mirror.
    /// </summary>
    [Fact]
    public void Validate_BatchWithRefusedCall_AppliesFollowingMessages()
    {
        // Arrange
        using A2UIMessageValidator validator = ToolsValidator(() => { });
        JsonArray batch =
        [
            RendererCall("""{"@call":"echo","catalogId":"urn:missing","args":{"value":"x"}}"""),
            A2UIMessages.CreateSurface("main", new Text { Content = "hi" }),
        ];

        // Act
        A2UIValidationResult result = validator.Validate(batch);

        // Assert
        result.Errors.ShouldHaveSingleItem().FunctionCallId.ShouldBe("c1");
        validator.Surfaces.ContainsKey("main").ShouldBeTrue();
    }

    /// <summary>
    /// Proves that a <c>callRendererFunction</c> on a surface the mirror does not hold is reported.
    /// </summary>
    [Fact]
    public void Validate_CallRendererFunctionUnknownSurface_ReportsInvalidFunctionCall()
    {
        // Arrange
        using A2UIMessageValidator validator = ToolsValidator(() => { });
        JsonObject message = RendererCall("""{"@call":"echo","catalogId":"urn:tools","args":{"value":"x"}}""");
        message["callRendererFunction"]!["surfaceId"] = "missing";

        // Act
        A2UIValidationResult result = validator.Validate(message);

        // Assert
        A2UIErrorReport error = result.Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe(A2UIErrorCodes.InvalidFunctionCall);
        error.FunctionCallId.ShouldBe("c1");
    }

    /// <summary>
    /// Creates a validator with the basic catalog and a tools catalog: <c>echo</c> (agent-callable, takes a string
    /// <c>value</c>), <c>gesture</c> (needs user activation) and <c>secret</c> (renderer-only).
    /// </summary>
    /// <param name="onRun">Called whenever any tools function runs.</param>
    private static A2UIMessageValidator ToolsValidator(Action onRun)
    {
        ValueTask<JsonNode?> Run(FunctionInvocation invocation)
        {
            onRun();
            return ValueTask.FromResult<JsonNode?>(null);
        }

        JsonObject schema = JsonNode.Parse("""{"type":"object","properties":{"value":{"type":"string"}},"required":["value"]}""")!.AsObject();
        FunctionDefinition[] functions =
        [
            new FunctionDefinition("echo") { AllowedCallers = AllowedCallers.RendererOrAgent, ArgsSchema = schema }.WithImplementation(Run),
            new FunctionDefinition("gesture") { AllowedCallers = AllowedCallers.RendererOrAgent, RequiresUserActivation = true }.WithImplementation(Run),
            new FunctionDefinition("secret").WithImplementation(Run),
        ];
        return new A2UIMessageValidator([BasicCatalog.Create(), new Catalog("urn:tools", [], functions, A2UIProtocol.Version)]);
    }

    /// <summary>
    /// Builds a <c>callRendererFunction</c> message.
    /// </summary>
    /// <param name="callFunction">The <c>callFunction</c> object, as JSON.</param>
    private static JsonObject RendererCall(string callFunction) =>
        JsonNode.Parse($$$"""{"version":"1.0","callRendererFunction":{"functionCallId":"c1","callFunction":{{{callFunction}}}}}""")!.AsObject();

    /// <summary>
    /// Proves that a line that is not JSON is reported rather than thrown.
    /// </summary>
    [Fact]
    public void ValidateLine_NotJson_ReportsError()
    {
        // Arrange
        using A2UIMessageValidator validator = new();

        // Act
        A2UIValidationResult result = validator.ValidateLine("{not json");

        // Assert
        result.Errors.ShouldHaveSingleItem().Code.ShouldBe(A2UIErrorCodes.ValidationFailed);
    }

    /// <summary>
    /// Proves that a disposed validator refuses to check.
    /// </summary>
    [Fact]
    public void Validate_Disposed_Throws()
    {
        // Arrange
        A2UIMessageValidator validator = new();
        validator.Dispose();

        // Act
        Action validate = () => validator.Validate(A2UIMessages.DeleteSurface("main"));

        // Assert
        validate.ShouldThrow<ObjectDisposedException>();
    }

    /// <summary>
    /// A component the basic catalog does not define.
    /// </summary>
    private sealed class Custom : A2UIComponent
    {
        /// <inheritdoc />
        public override string ComponentName => "Chart";

        /// <inheritdoc />
        protected internal override void WriteProperties(A2UIComponentWriter writer) => writer.Set("series", "a");
    }
}
