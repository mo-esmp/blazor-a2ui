using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BlazorA2UI.Catalogs;
using BlazorA2UI.Evaluation;
using BlazorA2UI.Expressions;

namespace BlazorA2UI.Functions;

/// <summary>
/// Renderer-side implementations of the basic catalog's functions.
/// </summary>
public static partial class BasicFunctions
{
    /// <summary>
    /// Returns every basic catalog function implementation, keyed by name.
    /// </summary>
    public static IReadOnlyDictionary<string, A2UIFunction> Create(CultureInfo? culture = null)
    {
        CultureInfo c = ResolveCulture(culture);
        return new Dictionary<string, A2UIFunction>(StringComparer.Ordinal)
        {
            ["required"] = Sync(Required),
            ["regex"] = Sync(RegexMatch),
            ["length"] = Sync(Length),
            ["numeric"] = Sync(Numeric),
            ["email"] = Sync(Email),
            ["formatString"] = FormatStringAsync,
            ["formatNumber"] = Sync(i => FormatNumber(i, c)),
            ["formatCurrency"] = Sync(i => FormatCurrency(i, c)),
            ["formatDate"] = Sync(i => FormatDate(i, c)),
            ["pluralize"] = Sync(i => Pluralize(i, c)),
            ["openUrl"] = OpenUrlAsync,
            ["and"] = Sync(i => Logical(i, all: true)),
            ["or"] = Sync(i => Logical(i, all: false)),
            ["not"] = Sync(i => !A2UIValues.ToBoolean(i.Arg("value"))),
        };
    }

    /// <summary>
    /// Returns the culture to format with: the given one, else the current culture, with the invariant culture replaced by en-US.
    /// </summary>
    private static CultureInfo ResolveCulture(CultureInfo? culture)
    {
        culture ??= CultureInfo.CurrentCulture;
        return string.IsNullOrEmpty(culture.Name) ? CultureInfo.GetCultureInfo("en-US") : culture;
    }

    /// <summary>
    /// Wraps a synchronous function as an <see cref="A2UIFunction"/>.
    /// </summary>
    private static A2UIFunction Sync(Func<FunctionInvocation, JsonNode?> f) => i => ValueTask.FromResult(f(i));

    /// <summary>
    /// Builds a check result; the message, code and severity are kept only when the check fails.
    /// </summary>
    private static JsonNode Valid(bool valid, string? message = null, string? code = null, string? severity = null) =>
        valid ? A2UIValues.ValidationResult(true) : A2UIValues.ValidationResult(false, message, code, severity);

    /// <summary>
    /// The <c>required</c> check: fails for null, an empty string and an empty list.
    /// </summary>
    private static JsonNode Required(FunctionInvocation i)
    {
        JsonNode? value = i.Arg("value");
        bool present = value switch
        {
            null => false,
            JsonArray arr => arr.Count > 0,
            JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>().Length > 0,
            JsonValue v when v.GetValueKind() == JsonValueKind.Null => false,
            _ => true,
        };
        return Valid(present, "This field is required.");
    }

    /// <summary>
    /// The <c>regex</c> check: matches the value with the A2UI regex dialect.
    /// </summary>
    /// <exception cref="A2UIExpressionException">The pattern is not valid or not safe.</exception>
    private static JsonNode RegexMatch(FunctionInvocation i)
    {
        string pattern = A2UIValues.AsString(i.Arg("pattern"))
                      ?? throw new A2UIExpressionException("regex needs a string pattern.");
        string value = A2UIValues.ToDisplayString(i.Arg("value"));
        Regex regex = A2UIRegex.Compile(pattern);
        if (A2UIRegex.CountCodePoints(value) > A2UIRegex.MaxValueCodePoints)
        {
            return Valid(false, "Value exceeds the maximum length for pattern matching.");
        }

        return Valid(A2UIRegex.IsMatch(pattern, value), "Value does not match required pattern.");
    }

    /// <summary>
    /// The <c>length</c> check: compares a string's length in code points, or a list's item count, with <c>min</c> and <c>max</c>.
    /// </summary>
    private static JsonNode Length(FunctionInvocation i)
    {
        JsonNode? value = i.Arg("value");
        int length = value switch
        {
            null => 0,
            JsonArray arr => arr.Count,
            _ => A2UIRegex.CountCodePoints(A2UIValues.ToDisplayString(value)),
        };
        double? min = A2UIValues.ToDouble(i.Arg("min"));
        double? max = A2UIValues.ToDouble(i.Arg("max"));
        if (min is { } lo && length < lo)
        {
            return Valid(false, $"Minimum length is {A2UIValues.FormatNumber(lo)}.");
        }

        return max is { } hi && length > hi ? Valid(false, $"Maximum length is {A2UIValues.FormatNumber(hi)}.") : Valid(true);
    }

    /// <summary>
    /// The <c>numeric</c> check: the value must parse as a number within <c>min</c> and <c>max</c>.
    /// </summary>
    private static JsonNode Numeric(FunctionInvocation i)
    {
        if (A2UIValues.ToDouble(i.Arg("value")) is not { } n || double.IsNaN(n))
        {
            return Valid(false, "Value must be a valid number.");
        }

        double? min = A2UIValues.ToDouble(i.Arg("min"));
        double? max = A2UIValues.ToDouble(i.Arg("max"));
        if (min is { } lo && n < lo)
        {
            return Valid(false, $"Minimum value is {A2UIValues.FormatNumber(lo)}.");
        }

        return max is { } hi && n > hi ? Valid(false, $"Maximum value is {A2UIValues.FormatNumber(hi)}.") : Valid(true);
    }

    [GeneratedRegex(@"^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex EmailPattern();

    /// <summary>
    /// The <c>email</c> check.
    /// </summary>
    private static JsonNode Email(FunctionInvocation i)
    {
        string? value = A2UIValues.AsString(i.Arg("value"));
        bool valid = value is not null && value.Length <= 320 && EmailPattern().IsMatch(value);
        return Valid(valid, "Please enter a valid email address (e.g. user@example.com).", "INVALID_EMAIL", "error");
    }

    /// <summary>
    /// The <c>formatString</c> function: evaluates a template's interpolations and joins the parts. A part that fails is reported and renders empty.
    /// </summary>
    /// <returns>The text, or null while a part is still pending.</returns>
    private static async ValueTask<JsonNode?> FormatStringAsync(FunctionInvocation i)
    {
        string template = A2UIValues.ToDisplayString(i.Arg("value"));
        List<JsonNode?> parts = ExpressionParser.Parse(template);
        i.Evaluator.PreflightTemplate(parts, i.Context, i.Scope);
        StringBuilder sb = new();
        bool wasInTemplate = i.Scope.InTemplate;
        i.Scope.InTemplate = true;
        try
        {
            foreach (JsonNode? part in parts)
            {
                if (part is JsonValue literal && literal.GetValueKind() == JsonValueKind.String)
                {
                    sb.Append(literal.GetValue<string>());
                    continue;
                }

                JsonNode? value;
                try
                {
                    value = await i.Evaluator.EvaluateAsync(part, i.Context, i.Scope).ConfigureAwait(false);
                }
                catch (A2UIException ex) when (i.Scope.ReportError is { } report && ex.Code != A2UIErrorCodes.Cancelled)
                {
                    // A failing template part is reported and renders empty; the rest of the text stays.
                    report(ex);
                    continue;
                }

                sb.Append(A2UIValues.ToDisplayString(value));
            }
        }
        finally
        {
            i.Scope.InTemplate = wasInTemplate;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Returns a copy of the culture's number format, without group separators when <paramref name="grouping"/> is false.
    /// </summary>
    private static NumberFormatInfo NumberFormat(CultureInfo culture, bool grouping)
    {
        NumberFormatInfo format = (NumberFormatInfo)culture.NumberFormat.Clone();
        if (!grouping)
        {
            format.NumberGroupSeparator = string.Empty;
            format.CurrencyGroupSeparator = string.Empty;
        }

        return format;
    }

    /// <summary>
    /// Reads the <c>grouping</c> argument; true when it is missing.
    /// </summary>
    private static bool Grouping(FunctionInvocation i) => i.Args["grouping"] is not { } g || A2UIValues.ToBoolean(g);

    /// <summary>
    /// The <c>formatNumber</c> function: formats with the requested fraction digits and grouping.
    /// </summary>
    /// <returns>The text, or an empty string when the value is not a number.</returns>
    private static JsonNode? FormatNumber(FunctionInvocation i, CultureInfo culture)
    {
        if (A2UIValues.ToDouble(i.Arg("value")) is not { } n)
        {
            return string.Empty;
        }

        NumberFormatInfo format = NumberFormat(culture, Grouping(i));
        if (A2UIValues.ToDouble(i.Arg("decimals")) is { } d)
        {
            int digits = (int)Math.Clamp(d, 0, 20);
            return Math.Round(n, digits, MidpointRounding.AwayFromZero).ToString("N" + digits, format);
        }

        double rounded = Math.Round(n, 3, MidpointRounding.AwayFromZero);
        string text = rounded.ToString("N3", format);
        string sep = format.NumberDecimalSeparator;
        if (text.Contains(sep, StringComparison.Ordinal))
        {
            text = text.TrimEnd('0');
            if (text.EndsWith(sep, StringComparison.Ordinal))
            {
                text = text[..^sep.Length];
            }
        }

        return text;
    }

    /// <summary>
    /// The <c>formatCurrency</c> function: formats an amount with an ISO 4217 code's symbol and digits.
    /// </summary>
    /// <returns>The text, or an empty string when the amount is not a number.</returns>
    private static JsonNode? FormatCurrency(FunctionInvocation i, CultureInfo culture)
    {
        if (A2UIValues.ToDouble(i.Arg("value")) is not { } n)
        {
            return string.Empty;
        }

        string code = (A2UIValues.AsString(i.Arg("currency")) ?? string.Empty).Trim().ToUpperInvariant();
        NumberFormatInfo format = NumberFormat(culture, Grouping(i));
        (string? symbol, int defaultDigits) = CurrencyInfo(code, culture);
        format.CurrencySymbol = symbol;
        int digits = A2UIValues.ToDouble(i.Arg("decimals")) is { } d ? (int)Math.Clamp(d, 0, 20) : defaultDigits;
        return Math.Round(n, digits, MidpointRounding.AwayFromZero).ToString("C" + digits, format);
    }

    /// <summary>
    /// Currency symbols and fraction digits by ISO code and culture, found once by scanning cultures.
    /// </summary>
    private static readonly Dictionary<string, (string Symbol, int Digits)> s_currencyCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Finds the symbol and fraction digits for a currency code. An unknown code is shown as the code itself, with two digits.
    /// </summary>
    private static (string Symbol, int Digits) CurrencyInfo(string code, CultureInfo culture)
    {
        if (code.Length != 3)
        {
            return (code, 2);
        }

        try
        {
            if (!string.IsNullOrEmpty(culture.Name) && new RegionInfo(culture.Name).ISOCurrencySymbol == code)
            {
                return (culture.NumberFormat.CurrencySymbol, culture.NumberFormat.CurrencyDecimalDigits);
            }
        }
        catch (ArgumentException)
        {
            // Neutral cultures have no region.
        }

        lock (s_currencyCache)
        {
            if (s_currencyCache.TryGetValue(code, out (string Symbol, int Digits) cached))
            {
                return cached;
            }

            (string, int) found = (code, 2);
            foreach (CultureInfo specific in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
            {
                try
                {
                    if (new RegionInfo(specific.Name).ISOCurrencySymbol == code)
                    {
                        found = (specific.NumberFormat.CurrencySymbol, specific.NumberFormat.CurrencyDecimalDigits);
                        if (specific.Name.EndsWith("-US", StringComparison.Ordinal) || code != "USD")
                        {
                            break;
                        }
                    }
                }
                catch (ArgumentException)
                {
                }
            }

            s_currencyCache[code] = found;
            return found;
        }
    }

    /// <summary>
    /// The <c>formatDate</c> function: formats a date with a TR35 pattern.
    /// </summary>
    /// <returns>The text, or an empty string when the value is not a date.</returns>
    private static JsonNode? FormatDate(FunctionInvocation i, CultureInfo culture)
    {
        string format = A2UIValues.AsString(i.Arg("format")) ?? "yyyy-MM-dd";
        if (!TryReadDate(i.Arg("value"), out DateTimeOffset date))
        {
            return string.Empty;
        }

        if (format == "ISO")
        {
            return date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        return Tr35DateFormatter.Format(date, format, culture);
    }

    /// <summary>
    /// The ISO 8601 forms a date string may take.
    /// </summary>
    private static readonly string[] s_dateFormats =
    [
        "yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mmK", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ssK", "HH:mm", "HH:mm:ss",
    ];

    /// <summary>
    /// Reads a date from epoch milliseconds or an ISO 8601 string.
    /// </summary>
    /// <returns>False when the value is neither, or the time is outside the range a <see cref="DateTimeOffset"/> can hold.</returns>
    private static bool TryReadDate(JsonNode? value, out DateTimeOffset date)
    {
        date = default;
        if (value is JsonValue v && v.GetValueKind() == JsonValueKind.Number && A2UIValues.ToDouble(v) is { } ms)
        {
            if (ms is < -62135596800000 or > 253402300799999)
            {
                return false;
            }

            date = DateTimeOffset.FromUnixTimeMilliseconds((long)ms);
            return true;
        }

        string? text = A2UIValues.AsString(value)?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return DateTimeOffset.TryParseExact(
            text,
            s_dateFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out date);
    }

    /// <summary>
    /// The <c>pluralize</c> function: an exact <c>zero</c>, <c>one</c> or <c>two</c> text wins for 0, 1 and 2; otherwise it picks the text for the count's CLDR plural category, then <c>other</c>, then an empty string.
    /// </summary>
    private static JsonNode? Pluralize(FunctionInvocation i, CultureInfo culture)
    {
        double n = A2UIValues.ToDouble(i.Arg("value")) ?? 0;
        string? Pick(string key) => i.Args.ContainsKey(key) ? A2UIValues.ToDisplayString(i.Args[key]) : null;

        // Exact matches for 0, 1 and 2 win when the agent supplied them.
        string? exact = n switch { 0 => Pick("zero"), 1 => Pick("one"), 2 => Pick("two"), _ => null };
        if (exact is not null)
        {
            return exact;
        }

        return Pick(PluralRules.Category(n, culture)) ?? Pick("other") ?? string.Empty;
    }

    /// <summary>
    /// The <c>openUrl</c> function: opens a URL that <see cref="UrlSafety.ValidateNavigationUrl"/> accepts through the host's <see cref="IA2UIUrlLauncher"/>. Without a launcher it does nothing.
    /// </summary>
    /// <exception cref="A2UIExpressionException">The URL is empty or not allowed.</exception>
    private static async ValueTask<JsonNode?> OpenUrlAsync(FunctionInvocation i)
    {
        string url = UrlSafety.ValidateNavigationUrl(A2UIValues.AsString(i.Arg("url")));
        if (i.Services?.GetService(typeof(IA2UIUrlLauncher)) is IA2UIUrlLauncher launcher)
        {
            await launcher.OpenAsync(url, i.CancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>
    /// The <c>and</c> and <c>or</c> functions.
    /// </summary>
    /// <param name="i">The invocation.</param>
    /// <param name="all">True for <c>and</c>, false for <c>or</c>.</param>
    /// <exception cref="A2UIExpressionException">Fewer than two values were given.</exception>
    private static JsonNode Logical(FunctionInvocation i, bool all)
    {
        if (i.Arg("values") is not JsonArray values || values.Count < 2)
        {
            throw new A2UIExpressionException($"'{i.Name}' needs at least two values.");
        }

        return all ? values.All(A2UIValues.ToBoolean) : values.Any(A2UIValues.ToBoolean);
    }
}
