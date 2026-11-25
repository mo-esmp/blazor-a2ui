using System.Globalization;
using System.Text;

namespace BlazorA2UI.Text;

/// <summary>
/// Unicode Standard Annex #31 identifier checks (<c>^[\p{XID_Start}_][\p{XID_Continue}]*$</c>),
/// used for component ids, catalog entity names, argument keys and extension keys.
/// </summary>
/// <remarks>
/// .NET has no XID property tables, so this approximates XID_Start and XID_Continue with
/// their defining general categories (L*, Nl / L*, Nl, Mn, Mc, Nd, Pc), which agree for
/// every code point used in practice.
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

    public static bool IsXidStart(Rune rune) => Rune.GetUnicodeCategory(rune) switch
    {
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.LetterNumber => true,
        _ => false,
    };

    public static bool IsXidContinue(Rune rune) => IsXidStart(rune) || Rune.GetUnicodeCategory(rune) switch
    {
        UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.ConnectorPunctuation => true,
        _ => false,
    };
}
