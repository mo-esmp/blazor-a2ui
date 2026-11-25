namespace BlazorA2UI.Tests.Media;

/// <summary>
/// Tests for <see cref="A2UIMediaOptions"/>, where the host registers the media allowlist.
/// </summary>
public sealed class A2UIMediaOptionsTests
{
    /// <summary>
    /// Proves that a prefix that is not a plain absolute http or https URL is refused, so a typo cannot widen the allowlist.
    /// </summary>
    /// <param name="prefix">The prefix the host tried to register.</param>
    [Theory]
    [InlineData("cdn.example.com/images/")]
    [InlineData("/images/")]
    [InlineData("ftp://cdn.example.com/")]
    [InlineData("https://user@cdn.example.com/")]
    [InlineData("https://cdn.example.com/?a=1")]
    [InlineData("https://cdn.example.com/#top")]
    public void Allow_InvalidPrefix_Throws(string prefix)
    {
        // Arrange
        A2UIMediaOptions options = new();

        // Act
        Action allow = () => options.Allow(prefix);

        // Assert
        allow.ShouldThrow<ArgumentException>();
    }
}
