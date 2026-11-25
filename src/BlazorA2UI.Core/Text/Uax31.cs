using System.Text;

namespace BlazorA2UI.Text;

/// <summary>
/// Unicode Standard Annex #31 identifier checks (<c>^[\p{XID_Start}_][\p{XID_Continue}]*$</c>),
/// used for component ids, catalog entity names, argument keys and extension keys.
/// </summary>
/// <remarks>
/// .NET has no XID property tables, so exact tables from the Unicode Character Database are
/// generated into <see cref="Uax31Tables"/>.
/// </remarks>
public static class Uax31
{
    public static bool IsIdentifier(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var first = true;
        foreach (var rune in value.EnumerateRunes())
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

    public static bool IsXidStart(Rune rune) => InRanges(Uax31Tables.XidStart, rune.Value);

    public static bool IsXidContinue(Rune rune) => InRanges(Uax31Tables.XidContinue, rune.Value);

    private static bool InRanges(int[] ranges, int value)
    {
        int lo = 0, hi = (ranges.Length / 2) - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
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
