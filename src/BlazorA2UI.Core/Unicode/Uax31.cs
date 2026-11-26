using System.Text;

namespace BlazorA2UI.Unicode;

/// <summary>
/// Unicode Standard Annex #31 identifier checks (<c>^[\p{XID_Start}_][\p{XID_Continue}]*$</c>),
/// used for component ids, catalog entity names, argument keys and extension keys.
/// </summary>
/// <remarks>
/// .NET has no XID property tables, so exact tables from the Unicode Character Database are
/// generated into <see cref="Uax31Tables"/>.
/// </remarks>
internal static class Uax31
{
    /// <summary>
    /// Whether <paramref name="value"/> is a UAX #31 identifier: an XID_Start code point or underscore, then XID_Continue code points.
    /// </summary>
    /// <returns>False for null and empty strings.</returns>
    public static bool IsIdentifier(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        bool first = true;
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (first)
            {
                if (rune.Value != '_' && !IsXidStart(rune))
                {
                    return false;
                }

                first = false;
            }
            else if (!IsXidContinue(rune))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a code point has the XID_Start property.
    /// </summary>
    public static bool IsXidStart(Rune rune) => InRanges(Uax31Tables.s_xidStart, rune.Value);

    /// <summary>
    /// Whether a code point has the XID_Continue property.
    /// </summary>
    public static bool IsXidContinue(Rune rune) => InRanges(Uax31Tables.s_xidContinue, rune.Value);

    /// <summary>
    /// Binary-searches sorted inclusive start/end pairs for <paramref name="value"/>.
    /// </summary>
    private static bool InRanges(int[] ranges, int value)
    {
        int lo = 0, hi = (ranges.Length / 2) - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (value < ranges[2 * mid])
            {
                hi = mid - 1;
            }
            else if (value > ranges[(2 * mid) + 1])
            {
                lo = mid + 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }
}
