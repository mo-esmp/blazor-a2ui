using System.Text;
using BlazorA2UI.Text;
using Shouldly;
using Xunit;

namespace BlazorA2UI.Tests.Text;

/// <summary>
/// Tests for <see cref="Uax31"/> identifier checks.
/// </summary>
public sealed class Uax31Tests
{
    /// <summary>
    /// Proves that identifiers follow the exact XID_Start and XID_Continue tables, including their exceptions.
    /// </summary>
    /// <param name="value">The candidate identifier.</param>
    /// <param name="expected">Whether it is an identifier.</param>
    [Theory]
    [InlineData("℘x", true)] // Other_ID_Start
    [InlineData("a·b", true)] // Other_ID_Continue
    [InlineData("a·", true)] // Other_ID_Continue
    [InlineData("ⸯ", false)] // Lm, but Pattern_Syntax
    [InlineData("゛", false)] // ID_Start, but not XID_Start
    [InlineData("_x1", true)]
    [InlineData("1x", false)]
    [InlineData("", false)]
    public void IsIdentifier_XidEdgeCases_FollowsTables(string value, bool expected)
    {
        // Arrange
        // (The value comes from the theory data.)

        // Act
        bool result = Uax31.IsIdentifier(value);

        // Assert
        result.ShouldBe(expected);
    }

    /// <summary>
    /// Proves that a letter outside the Basic Multilingual Plane can start an identifier.
    /// </summary>
    [Fact]
    public void IsIdentifier_SupplementaryLetter_ReturnsTrue()
    {
        // Arrange
        string value = new Rune(0x1D400).ToString() + "x";

        // Act
        bool result = Uax31.IsIdentifier(value);

        // Assert
        result.ShouldBeTrue();
    }
}
