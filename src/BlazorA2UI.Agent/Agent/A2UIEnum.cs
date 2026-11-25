namespace BlazorA2UI.Agent;

/// <summary>
/// Converts enumeration members to their wire names.
/// </summary>
internal static class A2UIEnum
{
    /// <summary>
    /// Returns a member's wire name: its C# name with the first letter in lower case, so <c>SpaceBetween</c> becomes
    /// <c>spaceBetween</c>. Every enumeration in the basic catalog follows that rule.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is not a defined member.</exception>
    /// <typeparam name="T">The enumeration type.</typeparam>
    /// <param name="value">The member.</param>
    public static string ToWire<T>(T value)
        where T : struct, Enum
    {
        string name = Enum.GetName(value) ?? throw new ArgumentOutOfRangeException(nameof(value), value, "The value is not a defined member.");
        return LowerFirst(name);
    }

    /// <summary>
    /// Returns a name with its first letter in lower case.
    /// </summary>
    /// <param name="name">A non-empty name.</param>
    public static string LowerFirst(string name) =>
        char.IsLower(name[0])
            ? name
            : string.Create(name.Length, name, static (span, source) =>
            {
                source.AsSpan().CopyTo(span);
                span[0] = char.ToLowerInvariant(span[0]);
            });
}
