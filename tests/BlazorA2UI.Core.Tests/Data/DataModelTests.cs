using System.Text.Json.Nodes;
using BlazorA2UI.Data;
using Shouldly;
using Xunit;

namespace BlazorA2UI.Tests.Data;

/// <summary>
/// Tests for <see cref="DataModel"/> writes that the conformance suites do not cover.
/// </summary>
public sealed class DataModelTests
{
    /// <summary>
    /// Proves that a write rejected for its list index creates none of the containers on its path.
    /// </summary>
    [Fact]
    public void Set_IndexAboveLimit_LeavesModelUnchanged()
    {
        // Arrange
        DataModel model = new(JsonNode.Parse("""{"name":"x"}"""));

        // Act
        Action write = () => model.Set("/newList/10001/value", 1);

        // Assert
        write.ShouldThrow<A2UIDataException>();
        model.Snapshot().ShouldNotBeNull().ToJsonString().ShouldBe("""{"name":"x"}""");
    }

    /// <summary>
    /// Proves that a write running into a primitive value creates none of the containers on its path.
    /// </summary>
    [Fact]
    public void Set_PathThroughPrimitive_LeavesModelUnchanged()
    {
        // Arrange
        DataModel model = new(JsonNode.Parse("""{"name":"x"}"""));

        // Act
        Action write = () => model.Set("/name/c/d", 1);

        // Assert
        write.ShouldThrow<A2UIDataException>();
        model.Snapshot().ShouldNotBeNull().ToJsonString().ShouldBe("""{"name":"x"}""");
    }

    /// <summary>
    /// Proves that a valid write still creates the missing lists and objects on its path.
    /// </summary>
    [Fact]
    public void Set_MissingContainers_CreatesThem()
    {
        // Arrange
        DataModel model = new();

        // Act
        model.Set("/items/1/name", "b");

        // Assert
        model.Snapshot().ShouldNotBeNull().ToJsonString().ShouldBe("""{"items":[null,{"name":"b"}]}""");
    }
}
