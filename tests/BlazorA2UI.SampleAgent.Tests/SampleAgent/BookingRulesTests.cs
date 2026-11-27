namespace BlazorA2UI.Tests.SampleAgent;

/// <summary>
/// Tests for <see cref="BookingRules"/>.
/// </summary>
public sealed class BookingRulesTests
{
    /// <summary>
    /// The day the tests run on, a Friday.
    /// </summary>
    private static readonly DateOnly s_today = new(2026, 11, 27);

    /// <summary>
    /// Proves that every request gets a sentence for the user: free, closed, or what to fix.
    /// </summary>
    /// <param name="date">The date the user picked.</param>
    /// <param name="guests">How many people, as JSON.</param>
    /// <param name="expected">The sentence.</param>
    [Theory]
    [InlineData("2026-11-28", "2", "A table is free. Press Book to reserve it.")]
    [InlineData("2026-11-28T19:30", "2", "A table is free. Press Book to reserve it.")]
    [InlineData("2026-11-28T19:30:00", "2", "A table is free. Press Book to reserve it.")]
    [InlineData("2026-11-28T25:90", "2", "Pick a date to see if a table is free.")]
    [InlineData("2026-11-28 and more", "2", "Pick a date to see if a table is free.")]
    [InlineData("2026-02-30", "2", "Pick a date to see if a table is free.")]
    [InlineData("2026-11-30", "2", "The restaurant is closed on Mondays.")]
    [InlineData("", "2", "Pick a date to see if a table is free.")]
    [InlineData("28/11/2026", "2", "Pick a date to see if a table is free.")]
    [InlineData("2026-11-28", "9", "Choose between 1 and 8 guests.")]
    [InlineData("2026-11-28", "2.5", "Choose between 1 and 8 guests.")]
    [InlineData("2026-11-28", "\"many\"", "Choose between 1 and 8 guests.")]
    [InlineData("2026-11-26", "2", "Pick a date in the next 90 days.")]
    [InlineData("2027-03-01", "2", "Pick a date in the next 90 days.")]
    public void CheckAvailability_Request_AnswersInWords(string date, string guests, string expected)
    {
        // Arrange
        JsonNode? guestsNode = JsonNode.Parse(guests);

        // Act
        string answer = BookingRules.CheckAvailability(JsonValue.Create(date), guestsNode, s_today);

        // Assert
        answer.ShouldBe(expected);
    }

    /// <summary>
    /// Proves that a missing date or party gets a sentence, not an exception.
    /// </summary>
    [Fact]
    public void CheckAvailability_NullArguments_AsksForADate()
    {
        // Act
        string answer = BookingRules.CheckAvailability(null, null, s_today);

        // Assert
        answer.ShouldBe("Pick a date to see if a table is free.");
    }

    /// <summary>
    /// Proves that a name is trimmed and shortened, and that a blank or non-text name is no name.
    /// </summary>
    [Fact]
    public void ReadName_Values_AreTrimmedAndBounded()
    {
        // Act
        string? trimmed = BookingRules.ReadName(JsonValue.Create("  Ada  "));
        string? blank = BookingRules.ReadName(JsonValue.Create("   "));
        string? number = BookingRules.ReadName(JsonValue.Create(7));
        string? longName = BookingRules.ReadName(JsonValue.Create(new string('a', 200)));

        // Assert
        trimmed.ShouldBe("Ada");
        blank.ShouldBeNull();
        number.ShouldBeNull();
        longName.ShouldNotBeNull().Length.ShouldBe(BookingRules.MaxNameLength);
    }
}
