namespace BlazorA2UI.Agent.Basic;

/// <summary>
/// Calls to the functions of the basic catalog. Each method returns an <see cref="A2UICall"/>, which converts to an
/// <see cref="A2UIValue"/> and can be a property value, a check condition or, with
/// <see cref="A2UIAction.FunctionCall"/>, an action.
/// </summary>
public static class BasicFunctions
{
    /// <summary>
    /// Checks that a value is present: not null, not an empty string and not an empty list.
    /// </summary>
    /// <param name="value">The value to check, usually a binding.</param>
    public static A2UICall Required(A2UIValue value) => Call("required", ("value", value));

    /// <summary>
    /// Checks that a string matches a regular expression. The renderer accepts only the A2UI regex dialect.
    /// </summary>
    /// <param name="value">The string to check.</param>
    /// <param name="pattern">The pattern.</param>
    public static A2UICall Regex(A2UIValue value, string pattern) => Call("regex", ("value", value), ("pattern", pattern));

    /// <summary>
    /// Checks a string's length.
    /// </summary>
    /// <exception cref="ArgumentException">Neither bound is given.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A bound is negative.</exception>
    /// <param name="value">The string to check.</param>
    /// <param name="min">The shortest length allowed, or null for no minimum.</param>
    /// <param name="max">The longest length allowed, or null for no maximum.</param>
    public static A2UICall Length(A2UIValue value, int? min = null, int? max = null)
    {
        RequireBound(min.HasValue || max.HasValue);
        ArgumentOutOfRangeException.ThrowIfNegative(min ?? 0, nameof(min));
        ArgumentOutOfRangeException.ThrowIfNegative(max ?? 0, nameof(max));
        return Call("length", ("value", value), ("min", Number(min)), ("max", Number(max)));
    }

    /// <summary>
    /// Checks that a number is within a range.
    /// </summary>
    /// <exception cref="ArgumentException">Neither bound is given.</exception>
    /// <param name="value">The number to check.</param>
    /// <param name="min">The smallest number allowed, or null for no minimum.</param>
    /// <param name="max">The largest number allowed, or null for no maximum.</param>
    public static A2UICall Numeric(A2UIValue value, double? min = null, double? max = null)
    {
        RequireBound(min.HasValue || max.HasValue);
        return Call("numeric", ("value", value), ("min", Number(min)), ("max", Number(max)));
    }

    /// <summary>
    /// Checks that a string is an email address.
    /// </summary>
    /// <param name="value">The string to check.</param>
    public static A2UICall Email(A2UIValue value) => Call("email", ("value", value));

    /// <summary>
    /// Formats a string with <c>${...}</c> placeholders, such as <c>"Hello, ${/user/name}"</c>.
    /// </summary>
    /// <param name="value">The template.</param>
    public static A2UICall FormatString(A2UIValue value) => Call("formatString", ("value", value));

    /// <summary>
    /// Formats a number in the renderer's locale.
    /// </summary>
    /// <param name="value">The number.</param>
    /// <param name="decimals">The number of decimals, or null for the default.</param>
    /// <param name="grouping">Whether to group thousands, or null for the default.</param>
    public static A2UICall FormatNumber(A2UIValue value, A2UIValue? decimals = null, A2UIValue? grouping = null) =>
        Call("formatNumber", ("value", value), ("decimals", decimals), ("grouping", grouping));

    /// <summary>
    /// Formats an amount of money in the renderer's locale.
    /// </summary>
    /// <param name="value">The amount.</param>
    /// <param name="currency">The ISO 4217 currency code, such as <c>EUR</c>.</param>
    /// <param name="decimals">The number of decimals, or null for the currency's default.</param>
    /// <param name="grouping">Whether to group thousands, or null for the default.</param>
    public static A2UICall FormatCurrency(A2UIValue value, A2UIValue currency, A2UIValue? decimals = null, A2UIValue? grouping = null) =>
        Call("formatCurrency", ("value", value), ("currency", currency), ("decimals", decimals), ("grouping", grouping));

    /// <summary>
    /// Formats a date with a Unicode (TR35) pattern, such as <c>yyyy-MM-dd</c>.
    /// </summary>
    /// <param name="value">The date, as ISO 8601 text or a timestamp.</param>
    /// <param name="format">The pattern.</param>
    public static A2UICall FormatDate(A2UIValue value, A2UIValue format) => Call("formatDate", ("value", value), ("format", format));

    /// <summary>
    /// Picks the text for a count by the renderer locale's plural rules.
    /// </summary>
    /// <param name="value">The count.</param>
    /// <param name="other">The text for the general case.</param>
    /// <param name="zero">The text for zero, where the locale has that category.</param>
    /// <param name="one">The text for one.</param>
    /// <param name="two">The text for two.</param>
    /// <param name="few">The text for a few.</param>
    /// <param name="many">The text for many.</param>
    public static A2UICall Pluralize(
        A2UIValue value,
        A2UIValue other,
        A2UIValue? zero = null,
        A2UIValue? one = null,
        A2UIValue? two = null,
        A2UIValue? few = null,
        A2UIValue? many = null) =>
        Call("pluralize", ("value", value), ("zero", zero), ("one", one), ("two", two), ("few", few), ("many", many), ("other", other));

    /// <summary>
    /// Opens a URL. The renderer runs it only from a user action, only for <c>http</c> and <c>https</c>, and only
    /// through a launcher the host registers.
    /// </summary>
    /// <param name="url">The URL.</param>
    public static A2UICall OpenUrl(A2UIValue url) => Call("openUrl", ("url", url));

    /// <summary>
    /// Returns whether every value is true.
    /// </summary>
    /// <exception cref="ArgumentException">Fewer than two values are given.</exception>
    /// <param name="values">The values, at least two.</param>
    public static A2UICall And(params ReadOnlySpan<A2UIValue> values) => Logical("and", values);

    /// <summary>
    /// Returns whether any value is true.
    /// </summary>
    /// <exception cref="ArgumentException">Fewer than two values are given.</exception>
    /// <param name="values">The values, at least two.</param>
    public static A2UICall Or(params ReadOnlySpan<A2UIValue> values) => Logical("or", values);

    /// <summary>
    /// Returns the opposite of a boolean.
    /// </summary>
    /// <param name="value">The boolean.</param>
    public static A2UICall Not(A2UIValue value) => Call("not", ("value", value));

    /// <summary>
    /// Returns the index of the current item inside a list template (<c>@index</c>).
    /// </summary>
    /// <param name="offset">A number added to the 0-based index, such as 1 for 1-based numbering; null for none.</param>
    public static A2UICall Index(A2UIValue? offset = null) => Call("@index", ("offset", offset));

    /// <summary>
    /// Rejects a range check with no bound.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="hasBound"/> is false.</exception>
    /// <param name="hasBound">Whether a minimum or a maximum is given.</param>
    private static void RequireBound(bool hasBound)
    {
        if (!hasBound)
        {
            throw new ArgumentException("Give a minimum, a maximum or both.");
        }
    }

    /// <summary>
    /// Converts an optional number to an optional literal.
    /// </summary>
    /// <param name="number">The number, or null.</param>
    private static A2UIValue? Number(double? number) => number is { } value ? value : null;

    /// <summary>
    /// Builds a call, leaving out arguments that are null.
    /// </summary>
    /// <param name="name">The function name.</param>
    /// <param name="args">The arguments.</param>
    private static A2UICall Call(string name, params ReadOnlySpan<(string Name, A2UIValue? Value)> args)
    {
        A2UICall call = new(name);
        foreach ((string argName, A2UIValue? value) in args)
        {
            if (value is not null)
            {
                call.Args[argName] = value;
            }
        }

        return call;
    }

    /// <summary>
    /// Builds an <c>and</c> or <c>or</c> call.
    /// </summary>
    /// <exception cref="ArgumentException">Fewer than two values are given.</exception>
    /// <param name="name">The function name.</param>
    /// <param name="values">The values.</param>
    private static A2UICall Logical(string name, ReadOnlySpan<A2UIValue> values)
    {
        if (values.Length < 2)
        {
            throw new ArgumentException($"'{name}' needs at least two values.", nameof(values));
        }

        JsonArray items = [];
        foreach (A2UIValue value in values)
        {
            ArgumentNullException.ThrowIfNull(value, nameof(values));
            items.Add(value.ToJson());
        }

        A2UICall call = new(name);
        call.Args["values"] = A2UIValue.FromWire(items);
        return call;
    }
}
