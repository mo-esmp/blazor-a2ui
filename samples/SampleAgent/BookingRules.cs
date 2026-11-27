using System.Globalization;
using BlazorA2UI.Evaluation;

namespace BlazorA2UI.SampleAgent;

/// <summary>
/// The sample restaurant's rules: when it is open and how many people one table seats. A real agent would ask a
/// booking system instead.
/// </summary>
internal static class BookingRules
{
    /// <summary>
    /// The most people one table seats.
    /// </summary>
    public const int MaxGuests = 8;

    /// <summary>
    /// How far ahead a table can be booked, in days.
    /// </summary>
    public const int MaxDaysAhead = 90;

    /// <summary>
    /// The longest name kept for a booking, in characters.
    /// </summary>
    public const int MaxNameLength = 80;

    /// <summary>
    /// Says whether a table is free, as a sentence to show the user. The arguments come from the browser, so anything
    /// unexpected gets an answer, never an exception.
    /// </summary>
    /// <returns>The sentence.</returns>
    /// <param name="date">The date the user picked, or null.</param>
    /// <param name="guests">How many people, or null.</param>
    /// <param name="today">Today's date.</param>
    public static string CheckAvailability(JsonNode? date, JsonNode? guests, DateOnly today) =>
        Check(date, guests, today, out _, out _) ?? "A table is free. Press Book to reserve it.";

    /// <summary>
    /// Checks a booking request.
    /// </summary>
    /// <returns>Why the request cannot be booked, written for the user, or null when it can.</returns>
    /// <param name="date">The date the user picked, or null.</param>
    /// <param name="guests">How many people, or null.</param>
    /// <param name="today">Today's date.</param>
    /// <param name="day">The date, when it is valid.</param>
    /// <param name="party">The number of people, when it is valid.</param>
    public static string? Check(JsonNode? date, JsonNode? guests, DateOnly today, out DateOnly day, out int party)
    {
        party = 0;
        if (!TryReadDate(date, out day))
        {
            return "Pick a date to see if a table is free.";
        }

        if (!TryReadGuests(guests, out party))
        {
            return $"Choose between 1 and {MaxGuests} guests.";
        }

        if (day < today || day > today.AddDays(MaxDaysAhead))
        {
            return $"Pick a date in the next {MaxDaysAhead} days.";
        }

        return day.DayOfWeek == DayOfWeek.Monday ? "The restaurant is closed on Mondays." : null;
    }

    /// <summary>
    /// Reads the name of a booking: trimmed and shortened, or null when it is missing or blank.
    /// </summary>
    /// <returns>The name, or null.</returns>
    /// <param name="name">The name the user typed.</param>
    public static string? ReadName(JsonNode? name)
    {
        string? text = A2UIValues.AsString(name)?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return text.Length <= MaxNameLength ? text : text[..MaxNameLength];
    }

    /// <summary>
    /// Reads a date. A date input may also send a time, which is ignored.
    /// </summary>
    /// <returns>Whether the value is a date.</returns>
    /// <param name="node">The value.</param>
    /// <param name="date">The date, when the value is one.</param>
    private static bool TryReadDate(JsonNode? node, out DateOnly date)
    {
        date = default;
        if (A2UIValues.AsString(node) is not { Length: >= 10 } text)
        {
            return false;
        }

        return DateOnly.TryParseExact(text.AsSpan(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    /// <summary>
    /// Reads the number of guests: a whole number from 1 to <see cref="MaxGuests"/>.
    /// </summary>
    /// <returns>Whether the value is such a number.</returns>
    /// <param name="node">The value.</param>
    /// <param name="guests">The number, when the value is one.</param>
    private static bool TryReadGuests(JsonNode? node, out int guests)
    {
        guests = 0;
        if (A2UIValues.ToDouble(node) is not { } number || number != Math.Floor(number) || number < 1 || number > MaxGuests)
        {
            return false;
        }

        guests = (int)number;
        return true;
    }
}
