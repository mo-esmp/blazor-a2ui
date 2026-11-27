using System.Globalization;

namespace BlazorA2UI.SampleAgent;

/// <summary>
/// The surfaces the sample shows: the welcome, the booking form and the confirmation. The scripted mode replays the
/// first two as the model's answers, and the prompt shows the form to a real model as an example.
/// </summary>
internal static class BookingSurfaces
{
    /// <summary>
    /// The id of the welcome surface.
    /// </summary>
    public const string WelcomeId = "welcome";

    /// <summary>
    /// The id of the booking form's surface.
    /// </summary>
    public const string BookingId = "booking";

    /// <summary>
    /// The action the welcome's button sends.
    /// </summary>
    public const string StartBookingAction = "startBooking";

    /// <summary>
    /// The action the form's button sends.
    /// </summary>
    public const string BookAction = "book";

    /// <summary>
    /// Where the form shows why a booking was refused.
    /// </summary>
    public const string ProblemPath = "/booking/problem";

    /// <summary>
    /// Builds the welcome: a line about the sample and a button that starts a booking. Any welcome already shown is
    /// replaced.
    /// </summary>
    /// <returns>The messages.</returns>
    public static IReadOnlyList<JsonObject> Welcome() =>
    [
        A2UIMessages.DeleteSurface(WelcomeId),
        A2UIMessages.CreateSurface(
            WelcomeId,
            new Column
            {
                Children =
                [
                    new Text { Content = "I can book you a table at the sample restaurant." },
                    new Button
                    {
                        Child = new Text { Content = "Book a table" },
                        Action = A2UIAction.Event(StartBookingAction),
                        Variant = ButtonVariant.Primary,
                    },
                ],
            }),
    ];

    /// <summary>
    /// Builds the booking form. A line under the inputs asks the agent's <c>checkAvailability</c> as the user types,
    /// the next shows why a booking was refused, and the button sends the <c>book</c> action with the form's values.
    /// Any form already shown is replaced.
    /// </summary>
    /// <returns>The messages.</returns>
    public static IReadOnlyList<JsonObject> Form()
    {
        A2UIValue name = A2UIValue.Path("/booking/name");
        A2UIValue date = A2UIValue.Path("/booking/date");
        A2UIValue guests = A2UIValue.Path("/booking/guests");
        A2UICall availability = new(SampleCatalog.CheckAvailability) { CatalogId = SampleCatalog.CatalogId };
        availability.Args["date"] = date;
        availability.Args["guests"] = guests;
        return
        [
            A2UIMessages.DeleteSurface(BookingId),
            A2UIMessages.CreateSurface(
                BookingId,
                new Card
                {
                    Child = new Column
                    {
                        Children =
                        [
                            new Text { Content = "Book a table" },
                            new TextField
                            {
                                Label = "Name",
                                Value = name,
                                Checks = [new A2UICheck(BasicFunctions.Required(name), "Enter the name for the booking.")],
                            },
                            new DateTimeInput { Label = "Date", Value = date, EnableDate = true, EnableTime = false },
                            new Slider { Label = "Guests", Min = 1, Max = BookingRules.MaxGuests, Steps = BookingRules.MaxGuests - 1, Value = guests },
                            new Text { Content = availability, Variant = TextVariant.Caption },
                            new Text { Content = A2UIValue.Path(ProblemPath) },
                            new Button
                            {
                                Child = new Text { Content = "Book" },
                                Action = A2UIAction.Event(BookAction, new Dictionary<string, A2UIValue> { ["name"] = name, ["date"] = date, ["guests"] = guests }),
                                Variant = ButtonVariant.Primary,
                            },
                        ],
                    },
                },
                dataModel: new JsonObject
                {
                    ["booking"] = new JsonObject { ["name"] = string.Empty, ["date"] = string.Empty, ["guests"] = 2, ["problem"] = string.Empty },
                }),
        ];
    }

    /// <summary>
    /// Builds the confirmation that replaces the form. The user's values go in the data model and the text reads them
    /// through bindings, so nothing the user typed becomes part of a template.
    /// </summary>
    /// <returns>The messages.</returns>
    /// <param name="surfaceId">The surface the booking came from.</param>
    /// <param name="name">The name for the booking.</param>
    /// <param name="day">The date.</param>
    /// <param name="guests">How many people.</param>
    /// <param name="timeZone">The user's time zone, as the renderer reported it.</param>
    public static IReadOnlyList<JsonObject> Confirmation(string surfaceId, string name, DateOnly day, int guests, string timeZone) =>
    [
        A2UIMessages.DeleteSurface(surfaceId),
        A2UIMessages.CreateSurface(
            surfaceId,
            new Card
            {
                Child = new Column
                {
                    Children =
                    [
                        new Text { Content = "Your table is booked." },
                        new Text { Content = BasicFunctions.FormatString("${/confirmation/guests} guests on ${/confirmation/date}, for ${/confirmation/name}.") },
                        new Text { Content = BasicFunctions.FormatString("We will send reminders in your time zone, ${/confirmation/timeZone}."), Variant = TextVariant.Caption },
                    ],
                },
            },
            dataModel: new JsonObject
            {
                ["confirmation"] = new JsonObject
                {
                    ["name"] = name,
                    ["date"] = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["guests"] = guests,
                    ["timeZone"] = timeZone,
                },
            }),
    ];

    /// <summary>
    /// Writes messages as one A2UI block of a model's answer, as <see cref="A2UIPromptBuilder"/> teaches.
    /// </summary>
    /// <returns>The block.</returns>
    /// <param name="messages">The messages.</param>
    public static string ToBlock(IReadOnlyList<JsonObject> messages)
    {
        JsonArray array = [];
        foreach (JsonObject message in messages)
        {
            array.Add(message.DeepClone());
        }

        return $"{A2UIResponseParser.OpenTag}\n{array.ToJsonString()}\n{A2UIResponseParser.CloseTag}";
    }
}
