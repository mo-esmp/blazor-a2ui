using System.Text;

namespace BlazorA2UI.Tests.Agent;

/// <summary>
/// Tests for <see cref="A2UIMessageWriter"/>.
/// </summary>
public sealed class A2UIMessageWriterTests
{
    /// <summary>
    /// Proves that each message is one compact line, and that a line break inside a string stays escaped.
    /// </summary>
    [Fact]
    public async Task WriteAsync_TwoMessages_WritesTwoLines()
    {
        // Arrange
        using MemoryStream stream = new();
        A2UIMessageWriter writer = new(stream);

        // Act
        await writer.WriteAsync(A2UIMessages.UpdateDataModel("main", "/note", "line one\nline two"));
        await writer.WriteAsync(A2UIMessages.DeleteSurface("main"));

        // Assert
        Encoding.UTF8.GetString(stream.ToArray()).ShouldBe(
            "{\"version\":\"1.0\",\"updateDataModel\":{\"surfaceId\":\"main\",\"path\":\"/note\",\"value\":\"line one\\nline two\"}}\n" +
            "{\"version\":\"1.0\",\"deleteSurface\":{\"surfaceId\":\"main\"}}\n");
    }

    /// <summary>
    /// Proves that the writer reads back through the renderer core line by line.
    /// </summary>
    [Fact]
    public async Task WriteAsync_Surface_RoundTripsThroughValidator()
    {
        // Arrange
        using MemoryStream stream = new();
        A2UIMessageWriter writer = new(stream);
        using A2UIMessageValidator validator = new();
        await writer.WriteAsync(A2UIMessages.CreateSurface("main", new Text { Content = "Hi" }));
        await writer.WriteAsync(A2UIMessages.UpdateDataModel("main", "/n", 1));

        // Act
        string[] lines = Encoding.UTF8.GetString(stream.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Assert
        lines.Length.ShouldBe(2);
        foreach (string line in lines)
        {
            validator.ValidateLine(line).IsValid.ShouldBeTrue();
        }
    }

    /// <summary>
    /// Proves that a stream that cannot be written is refused up front.
    /// </summary>
    [Fact]
    public void Constructor_ReadOnlyStream_Throws()
    {
        // Arrange
        using MemoryStream stream = new([], writable: false);

        // Act
        Func<A2UIMessageWriter> create = () => new A2UIMessageWriter(stream);

        // Assert
        create.ShouldThrow<ArgumentException>();
    }
}
