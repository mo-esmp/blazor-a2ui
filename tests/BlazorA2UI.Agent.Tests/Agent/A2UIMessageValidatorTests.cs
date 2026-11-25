using BlazorA2UI.Catalogs;
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
    /// Proves that a <c>callRendererFunction</c> envelope is checked against the catalog but its function never runs,
    /// and that a call to a function the agent may not call is reported.
    /// </summary>
    [Fact]
    public void Validate_CallRendererFunction_ChecksWithoutRunning()
    {
        // Arrange
        int runs = 0;
        FunctionDefinition echo = new FunctionDefinition("echo") { AllowedCallers = AllowedCallers.RendererOrAgent }
            .WithImplementation(_ =>
            {
                runs++;
                return ValueTask.FromResult<JsonNode?>(null);
            });
        using A2UIMessageValidator validator = new([BasicCatalog.Create(), new Catalog("urn:tools", [], [echo], A2UIProtocol.Version)]);
        JsonObject call = JsonNode.Parse("""
            {"version":"1.0","callRendererFunction":{"functionCallId":"c1","callFunction":{"@call":"echo","catalogId":"urn:tools"}}}
            """)!.AsObject();
        JsonObject rendererOnly = JsonNode.Parse("""
            {"version":"1.0","callRendererFunction":{"functionCallId":"c2","callFunction":{"@call":"formatString","args":{"value":"x"}}}}
            """)!.AsObject();

        // Act
        A2UIValidationResult valid = validator.Validate(call);
        A2UIValidationResult invalid = validator.Validate(rendererOnly);

        // Assert
        valid.Errors.ShouldBeEmpty();
        invalid.IsValid.ShouldBeFalse();
        runs.ShouldBe(0);
    }

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
