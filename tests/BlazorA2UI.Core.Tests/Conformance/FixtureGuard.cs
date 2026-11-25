﻿namespace BlazorA2UI.Tests.Conformance;

/// <summary>
/// Turns a missing field in a conformance case into a clear test failure instead of a null reference.
/// </summary>
internal static class FixtureGuard
{
    /// <summary>
    /// Returns <paramref name="value"/> when the case provides it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is null, so the case is malformed.</exception>
    public static T Required<T>(this T? value)
        where T : class =>
        value ?? throw new InvalidOperationException($"A conformance case is missing a required {typeof(T).Name}.");

    /// <summary>
    /// Returns the value of <paramref name="value"/> when the case provides it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is null, so the case is malformed.</exception>
    public static T Required<T>(this T? value)
        where T : struct =>
        value ?? throw new InvalidOperationException($"A conformance case is missing a required {typeof(T).Name}.");
}
