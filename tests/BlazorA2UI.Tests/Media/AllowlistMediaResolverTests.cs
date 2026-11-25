using Microsoft.Extensions.Options;

namespace BlazorA2UI.Tests.Media;

/// <summary>
/// Tests for <see cref="AllowlistMediaResolver"/>, the gate every model-supplied media URL passes through.
/// </summary>
public sealed class AllowlistMediaResolverTests
{
    /// <summary>
    /// Proves that a URL under an allowed prefix resolves to itself.
    /// </summary>
    [Fact]
    public void Resolve_UrlUnderPrefix_ReturnsUrl()
    {
        // Arrange
        AllowlistMediaResolver resolver = Create("https://cdn.example.com/images/");

        // Act
        Uri? resolved = resolver.Resolve(Request(A2UIMediaKind.Image, "https://cdn.example.com/images/cat.png"));

        // Assert
        resolved.ShouldNotBeNull().AbsoluteUri.ShouldBe("https://cdn.example.com/images/cat.png");
    }

    /// <summary>
    /// Proves that nothing resolves when the host registered no prefix.
    /// </summary>
    [Fact]
    public void Resolve_NoPrefixes_ReturnsNull()
    {
        // Arrange
        AllowlistMediaResolver resolver = new(Options.Create(new A2UIMediaOptions()));

        // Act
        Uri? resolved = resolver.Resolve(Request(A2UIMediaKind.Image, "https://cdn.example.com/images/cat.png"));

        // Assert
        resolved.ShouldBeNull();
    }

    /// <summary>
    /// Proves that URLs outside the prefix, with another scheme, user info, encoded separators or tricks on the
    /// host or path are all blocked.
    /// </summary>
    /// <param name="url">The model-supplied URL.</param>
    [Theory]
    [InlineData("http://cdn.example.com/images/cat.png")]
    [InlineData("https://cdn.example.com.evil.test/images/cat.png")]
    [InlineData("https://evil.test/cdn.example.com/images/cat.png")]
    [InlineData("https://cdn.example.com:8443/images/cat.png")]
    [InlineData("https://user:pass@cdn.example.com/images/cat.png")]
    [InlineData("https://cdn.example.com/images/../secret.png")]
    [InlineData("https://cdn.example.com/images/%2e%2e/secret.png")]
    [InlineData("https://cdn.example.com/images/..%2fsecret.png")]
    [InlineData("https://cdn.example.com/images-private/cat.png")]
    [InlineData("https://cdn.example.com/other/cat.png")]
    [InlineData("/images/cat.png")]
    [InlineData("//cdn.example.com/images/cat.png")]
    [InlineData("data:image/png;base64,iVBORw0KGgo=")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("")]
    public void Resolve_UrlOutsideAllowlist_ReturnsNull(string url)
    {
        // Arrange
        AllowlistMediaResolver resolver = Create("https://cdn.example.com/images/");

        // Act
        Uri? resolved = resolver.Resolve(Request(A2UIMediaKind.Image, url));

        // Assert
        resolved.ShouldBeNull();
    }

    /// <summary>
    /// Proves that a prefix without a trailing slash matches only whole path segments.
    /// </summary>
    [Fact]
    public void Resolve_PrefixWithoutSlash_MatchesWholeSegmentsOnly()
    {
        // Arrange
        AllowlistMediaResolver resolver = Create("https://cdn.example.com/images");

        // Act
        Uri? inside = resolver.Resolve(Request(A2UIMediaKind.Image, "https://cdn.example.com/images/cat.png"));
        Uri? sibling = resolver.Resolve(Request(A2UIMediaKind.Image, "https://cdn.example.com/imagesX/cat.png"));

        // Assert
        inside.ShouldNotBeNull();
        sibling.ShouldBeNull();
    }

    /// <summary>
    /// Proves that a prefix allowed for some media kinds does not admit the others.
    /// </summary>
    [Fact]
    public void Resolve_KindNotAllowed_ReturnsNull()
    {
        // Arrange
        AllowlistMediaResolver resolver = Create("https://cdn.example.com/", A2UIMediaKind.Image);

        // Act
        Uri? image = resolver.Resolve(Request(A2UIMediaKind.Image, "https://cdn.example.com/a.png"));
        Uri? video = resolver.Resolve(Request(A2UIMediaKind.Video, "https://cdn.example.com/a.mp4"));

        // Assert
        image.ShouldNotBeNull();
        video.ShouldBeNull();
    }

    /// <summary>
    /// Proves that a URL longer than <see cref="AllowlistMediaResolver.MaxUrlLength"/> is blocked even under a prefix.
    /// </summary>
    [Fact]
    public void Resolve_UrlTooLong_ReturnsNull()
    {
        // Arrange
        AllowlistMediaResolver resolver = Create("https://cdn.example.com/");
        string url = "https://cdn.example.com/" + new string('a', AllowlistMediaResolver.MaxUrlLength);

        // Act
        Uri? resolved = resolver.Resolve(Request(A2UIMediaKind.Image, url));

        // Assert
        resolved.ShouldBeNull();
    }

    /// <summary>
    /// Builds a resolver that allows one prefix.
    /// </summary>
    /// <param name="prefix">The allowed prefix.</param>
    /// <param name="kinds">The kinds it allows; all kinds when empty.</param>
    private static AllowlistMediaResolver Create(string prefix, params A2UIMediaKind[] kinds) =>
        new(Options.Create(new A2UIMediaOptions().Allow(prefix, kinds)));

    /// <summary>
    /// Builds a request for a component of the test surface.
    /// </summary>
    /// <param name="kind">The media kind.</param>
    /// <param name="url">The model-supplied URL.</param>
    private static A2UIMediaRequest Request(A2UIMediaKind kind, string url) => new(kind, url, "s", "c");
}
