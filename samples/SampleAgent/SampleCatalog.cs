namespace BlazorA2UI.SampleAgent;

/// <summary>
/// The sample's own catalog. It adds two functions to the basic catalog's components: <c>checkAvailability</c>, which
/// the agent implements and the renderer calls from a binding, and <c>getTimeZone</c>, which the renderer implements
/// and the agent calls while it handles a booking.
/// </summary>
/// <remarks>
/// The renderer samples register the same catalog, with an implementation of <c>getTimeZone</c>, so both sides agree on
/// who runs which function.
/// </remarks>
public static class SampleCatalog
{
    /// <summary>
    /// The catalog's id.
    /// </summary>
    public const string CatalogId = "urn:blazor-a2ui:samples:booking";

    /// <summary>
    /// The agent function that says whether a table is free.
    /// </summary>
    public const string CheckAvailability = "checkAvailability";

    /// <summary>
    /// The renderer function that returns the user's IANA time zone.
    /// </summary>
    public const string GetTimeZone = "getTimeZone";

    /// <summary>
    /// Creates the catalog. Neither function has an implementation here: the agent registers a handler for
    /// <c>checkAvailability</c>, and a renderer attaches one for <c>getTimeZone</c> with
    /// <see cref="Catalog.WithFunctions"/>.
    /// </summary>
    /// <returns>The catalog.</returns>
    public static Catalog Create() => new(
        CatalogId,
        [],
        [
            new FunctionDefinition(CheckAvailability)
            {
                ReturnType = "string",
                Description = "Says whether the restaurant has a table for the party on the date. Returns a sentence to show the user.",
                ArgsSchema = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["date"] = new JsonObject { ["type"] = "string", ["description"] = "The date, as yyyy-MM-dd." },
                        ["guests"] = new JsonObject { ["type"] = "number", ["description"] = "How many people." },
                    },
                    ["required"] = new JsonArray("date", "guests"),
                },
            },
            new FunctionDefinition(GetTimeZone)
            {
                ReturnType = "string",
                AllowedCallers = AllowedCallers.AgentOnly,
                Description = "Returns the user's IANA time zone, such as Europe/Berlin.",
            },
        ],
        A2UIProtocol.Version,
        "checkAvailability tells the user whether a table is free; bind a Text to it with the form's date and guests.");
}
