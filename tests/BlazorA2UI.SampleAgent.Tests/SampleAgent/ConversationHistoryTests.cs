namespace BlazorA2UI.Tests.SampleAgent;

/// <summary>
/// Tests for <see cref="ConversationHistory"/>.
/// </summary>
public sealed class ConversationHistoryTests
{
    /// <summary>
    /// Proves that a conversation keeps only its latest messages.
    /// </summary>
    [Fact]
    public void Add_PastMessageLimit_KeepsLatest()
    {
        // Arrange
        ConversationHistory history = new(maxConversations: 10, maxMessages: 2);

        // Act
        history.Add("c1", new ChatMessage(ChatRole.User, "one"), new ChatMessage(ChatRole.Assistant, "two"));
        history.Add("c1", new ChatMessage(ChatRole.User, "three"));

        // Assert
        history.Get("c1").Select(m => m.Text).ShouldBe(["two", "three"]);
    }

    /// <summary>
    /// Proves that past the conversation limit the conversation used longest ago is dropped, and that reading a
    /// conversation counts as using it.
    /// </summary>
    [Fact]
    public void Add_PastConversationLimit_DropsLeastRecentlyUsed()
    {
        // Arrange
        ConversationHistory history = new(maxConversations: 2, maxMessages: 5);
        history.Add("a", new ChatMessage(ChatRole.User, "a"));
        history.Add("b", new ChatMessage(ChatRole.User, "b"));
        _ = history.Get("a");

        // Act
        history.Add("c", new ChatMessage(ChatRole.User, "c"));

        // Assert
        history.Count.ShouldBe(2);
        history.Get("b").ShouldBeEmpty();
        history.Get("a").ShouldHaveSingleItem().Text.ShouldBe("a");
    }

    /// <summary>
    /// Proves that the messages returned are a copy, so a turn's changes do not reach the history.
    /// </summary>
    [Fact]
    public void Get_ReturnsCopy()
    {
        // Arrange
        ConversationHistory history = new();
        history.Add("c1", new ChatMessage(ChatRole.User, "one"));

        // Act
        history.Get("c1").Add(new ChatMessage(ChatRole.User, "two"));

        // Assert
        history.Get("c1").ShouldHaveSingleItem();
    }
}
