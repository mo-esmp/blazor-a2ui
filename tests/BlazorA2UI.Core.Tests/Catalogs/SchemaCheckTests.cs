using System.Text.Json.Nodes;
using BlazorA2UI.Catalogs;
using Shouldly;
using Xunit;

namespace BlazorA2UI.Tests.Catalogs;

/// <summary>
/// Tests for <see cref="SchemaCheck"/>.
/// </summary>
public sealed class SchemaCheckTests
{
    /// <summary>
    /// The schema the <c>oneOf</c> tests use: an integer matches both branches.
    /// </summary>
    private static readonly JsonNode s_numberOrInteger = JsonNode.Parse("""{"oneOf":[{"type":"number"},{"type":"integer"}]}""")
                                                        ?? throw new InvalidOperationException("The test schema did not parse.");

    /// <summary>
    /// Proves that a value matching more than one <c>oneOf</c> branch is rejected.
    /// </summary>
    [Fact]
    public void Validate_OneOfWithTwoMatchingBranches_ReturnsProblem()
    {
        // Arrange
        JsonNode value = 5;

        // Act
        string? problem = SchemaCheck.Validate(value, s_numberOrInteger, null);

        // Assert
        problem.ShouldNotBeNull();
    }

    /// <summary>
    /// Proves that a value matching exactly one <c>oneOf</c> branch is accepted.
    /// </summary>
    [Fact]
    public void Validate_OneOfWithOneMatchingBranch_ReturnsNull()
    {
        // Arrange
        JsonNode value = 5.5;

        // Act
        string? problem = SchemaCheck.Validate(value, s_numberOrInteger, null);

        // Assert
        problem.ShouldBeNull();
    }

    /// <summary>
    /// Proves that a value matching no <c>oneOf</c> branch is rejected.
    /// </summary>
    [Fact]
    public void Validate_OneOfWithNoMatchingBranch_ReturnsProblem()
    {
        // Arrange
        JsonNode value = "x";

        // Act
        string? problem = SchemaCheck.Validate(value, s_numberOrInteger, null);

        // Assert
        problem.ShouldNotBeNull();
    }
}
