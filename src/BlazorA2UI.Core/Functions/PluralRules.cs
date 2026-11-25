using System.Globalization;

namespace BlazorA2UI.Functions;

/// <summary>CLDR cardinal plural categories for the languages most UIs ship in.</summary>
internal static class PluralRules
{
    public static string Category(double n, CultureInfo culture)
    {
        string lang = culture.TwoLetterISOLanguageName;
        bool isInt = n == Math.Floor(n);
        long i = isInt ? (long)Math.Abs(n) : -1;
        switch (lang)
        {
            case "ja" or "zh" or "ko" or "th" or "vi" or "id" or "ms" or "tr" or "fa":
                return lang is "tr" or "fa" && n == 1 ? "one" : "other";
            case "fr":
                return Math.Abs(n) < 2 ? "one" : "other";
            case "pt":
                return culture.Name == "pt-PT"
                    ? isInt && i == 1 ? "one" : "other"
                    : isInt && i <= 1 ? "one" : "other";
            case "ru" or "uk" or "be":
                if (!isInt)
                {
                    return "other";
                }

                if (i % 10 == 1 && i % 100 != 11)
                {
                    return "one";
                }

                if (i % 10 is >= 2 and <= 4 && i % 100 is < 12 or > 14)
                {
                    return "few";
                }

                return "many";
            case "pl":
                if (!isInt)
                {
                    return "other";
                }

                if (i == 1)
                {
                    return "one";
                }

                return i % 10 is >= 2 and <= 4 && i % 100 is < 12 or > 14 ? "few" : "many";
            case "cs" or "sk":
                if (!isInt)
                {
                    return "many";
                }

                return i == 1 ? "one" : i is >= 2 and <= 4 ? "few" : "other";
            case "ar":
                if (!isInt)
                {
                    return "other";
                }

                return i switch
                {
                    0 => "zero",
                    1 => "one",
                    2 => "two",
                    _ when i % 100 is >= 3 and <= 10 => "few",
                    _ when i % 100 is >= 11 and <= 99 => "many",
                    _ => "other",
                };
            case "he":
                return i == 1 ? "one" : i == 2 ? "two" : "other";
            default:
                return isInt && i == 1 ? "one" : "other";
        }
    }
}
