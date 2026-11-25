using System.Globalization;
using System.Text;

namespace BlazorA2UI.Functions;

/// <summary>
/// Formats dates with Unicode TR35 patterns (<c>yyyy-MM-dd</c>, <c>EEE MMM d, h:mm a</c>, ...).
/// </summary>
internal static class Tr35DateFormatter
{
    /// <summary>
    /// Formats <paramref name="value"/> with a TR35 pattern; quoted text is copied and unknown letters are kept as they are.
    /// </summary>
    public static string Format(DateTimeOffset value, string pattern, CultureInfo culture)
    {
        DateTimeFormatInfo info = culture.DateTimeFormat;
        DateTime dt = value.DateTime;
        StringBuilder sb = new();
        int i = 0;
        while (i < pattern.Length)
        {
            char c = pattern[i];
            if (c == '\'')
            {
                if (i + 1 < pattern.Length && pattern[i + 1] == '\'')
                {
                    sb.Append('\'');
                    i += 2;
                    continue;
                }

                int end = pattern.IndexOf('\'', i + 1);
                if (end < 0)
                {
                    sb.Append(pattern, i + 1, pattern.Length - i - 1);
                    break;
                }

                sb.Append(pattern, i + 1, end - i - 1);
                i = end + 1;
                continue;
            }

            if (!char.IsAsciiLetter(c))
            {
                sb.Append(c);
                i++;
                continue;
            }

            int count = 1;
            while (i + count < pattern.Length && pattern[i + count] == c)
            {
                count++;
            }

            sb.Append(Field(c, count, dt, value.Offset, info, culture));
            i += count;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Formats a number with at least <paramref name="width"/> digits.
    /// </summary>
    private static string Pad(long value, int width) => value.ToString(new string('0', Math.Max(1, width)), CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats one pattern field, such as <c>yyyy</c> or <c>MMM</c>, from its letter and repeat count.
    /// </summary>
    private static string Field(char c, int count, DateTime dt, TimeSpan offset, DateTimeFormatInfo info, CultureInfo culture)
    {
        switch (c)
        {
            case 'y' or 'u' or 'Y':
                return count == 2 ? Pad(dt.Year % 100, 2) : Pad(dt.Year, count);
            case 'M' or 'L':
                return count switch
                {
                    1 => dt.Month.ToString(CultureInfo.InvariantCulture),
                    2 => Pad(dt.Month, 2),
                    3 => info.GetAbbreviatedMonthName(dt.Month),
                    4 => info.GetMonthName(dt.Month),
                    _ => info.GetMonthName(dt.Month)[..1],
                };
            case 'd':
                return Pad(dt.Day, count);
            case 'D':
                return Pad(dt.DayOfYear, count);
            case 'E' or 'e' or 'c':
                if (c != 'E' && count <= 2)
                {
                    return Pad(((int)dt.DayOfWeek + 7 - (int)info.FirstDayOfWeek) % 7 + 1, count);
                }

                return count switch
                {
                    <= 3 => info.GetAbbreviatedDayName(dt.DayOfWeek),
                    4 => info.GetDayName(dt.DayOfWeek),
                    5 => info.GetDayName(dt.DayOfWeek)[..1],
                    _ => info.GetShortestDayName(dt.DayOfWeek),
                };
            case 'a' or 'b' or 'B':
                return dt.Hour < 12 ? info.AMDesignator is { Length: > 0 } am ? am : "AM" : info.PMDesignator is { Length: > 0 } pm ? pm : "PM";
            case 'h':
                return Pad(dt.Hour % 12 == 0 ? 12 : dt.Hour % 12, count);
            case 'H':
                return Pad(dt.Hour, count);
            case 'K':
                return Pad(dt.Hour % 12, count);
            case 'k':
                return Pad(dt.Hour == 0 ? 24 : dt.Hour, count);
            case 'm':
                return Pad(dt.Minute, count);
            case 's':
                return Pad(dt.Second, count);
            case 'S':
                {
                    string fraction = (dt.Ticks % TimeSpan.TicksPerSecond).ToString("0000000", CultureInfo.InvariantCulture);
                    return count <= 7 ? fraction[..count] : fraction.PadRight(count, '0');
                }

            case 'G':
                return count == 4 ? "Anno Domini" : "AD";
            case 'Q' or 'q':
                return ((dt.Month - 1) / 3 + 1).ToString(CultureInfo.InvariantCulture);
            case 'X' when offset == TimeSpan.Zero:
                return "Z";
            case 'Z' or 'x' or 'X' or 'O' or 'z':
                {
                    string sign = offset < TimeSpan.Zero ? "-" : "+";
                    TimeSpan abs = offset.Duration();
                    if (c is 'O' or 'z')
                    {
                        return $"GMT{sign}{abs.Hours}{(abs.Minutes > 0 ? ":" + Pad(abs.Minutes, 2) : string.Empty)}";
                    }

                    return count is 1 or 2 && c != 'Z'
                        ? $"{sign}{Pad(abs.Hours, 2)}{(count == 1 && abs.Minutes == 0 ? string.Empty : Pad(abs.Minutes, 2))}"
                        : count >= 3 && c != 'Z' || count == 5
                            ? $"{sign}{Pad(abs.Hours, 2)}:{Pad(abs.Minutes, 2)}"
                            : $"{sign}{Pad(abs.Hours, 2)}{Pad(abs.Minutes, 2)}";
                }

            default:
                return new string(c, count);
        }
    }
}
