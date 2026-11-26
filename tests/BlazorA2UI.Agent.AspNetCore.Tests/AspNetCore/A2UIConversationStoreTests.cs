using BlazorA2UI.Catalogs;

namespace BlazorA2UI.Tests.AspNetCore;

/// <summary>
/// Tests for <see cref="A2UIConversationStore"/>.
/// </summary>
public sealed class A2UIConversationStoreTests
{
    /// <summary>
    /// Proves that ids are random, well formed and unique.
    /// </summary>
    [Fact]
    public void Create_TwoConversations_HaveDistinctWellFormedIds()
    {
        // Arrange
        using A2UIConversationStore store = new(Options(new ManualTimeProvider()));

        // Act
        A2UIConversation first = store.Create().ShouldNotBeNull();
        A2UIConversation second = store.Create().ShouldNotBeNull();

        // Assert
        A2UIConversationStore.IsWellFormedId(first.Id).ShouldBeTrue();
        first.Id.ShouldNotBe(second.Id);
        store.Count.ShouldBe(2);
    }

    /// <summary>
    /// Proves that an idle conversation expires and is removed, but one with a request in progress does not.
    /// </summary>
    [Fact]
    public void Acquire_IdlePastTimeout_ExpiresOnlyWhenNoRequestIsActive()
    {
        // Arrange
        ManualTimeProvider clock = new();
        using A2UIConversationStore store = new(Options(clock));
        A2UIConversation busy = store.Create().ShouldNotBeNull();
        A2UIConversation idle = store.Create().ShouldNotBeNull();
        store.Release(idle);
        clock.Advance(TimeSpan.FromMinutes(31));

        // Act
        A2UIConversation? expired = store.Acquire(idle.Id);
        store.Sweep();

        // Assert
        expired.ShouldBeNull();
        store.Acquire(busy.Id).ShouldBeSameAs(busy);
        store.Count.ShouldBe(1);
    }

    /// <summary>
    /// Proves that a full store makes room by removing expired conversations before refusing a new one.
    /// </summary>
    [Fact]
    public void Create_FullWithExpired_MakesRoom()
    {
        // Arrange
        ManualTimeProvider clock = new();
        A2UIAgentEndpointOptions options = Options(clock);
        options.MaxConversations = 1;
        using A2UIConversationStore store = new(options);
        A2UIConversation first = store.Create().ShouldNotBeNull();

        // Act
        A2UIConversation? whileBusy = store.Create();
        store.Release(first);
        clock.Advance(TimeSpan.FromHours(1));
        A2UIConversation? afterExpiry = store.Create();

        // Assert
        whileBusy.ShouldBeNull();
        afterExpiry.ShouldNotBeNull();
    }

    /// <summary>
    /// Proves that malformed ids are refused without a lookup.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <param name="expected">Whether it is well formed.</param>
    [Theory]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAA", true)]
    [InlineData("abc-_DEF0123456789xyzA", true)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAA", false)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAA=", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsWellFormedId_Shapes_AreChecked(string? id, bool expected)
    {
        // Arrange
        string? candidate = id;

        // Act
        bool result = A2UIConversationStore.IsWellFormedId(candidate);

        // Assert
        result.ShouldBe(expected);
    }

    /// <summary>
    /// Builds options with the basic catalog and a clock.
    /// </summary>
    /// <param name="clock">The clock.</param>
    private static A2UIAgentEndpointOptions Options(TimeProvider clock)
    {
        A2UIAgentEndpointOptions options = new() { TimeProvider = clock };
        options.Catalogs.Add(BasicCatalog.Create());
        return options;
    }

    /// <summary>
    /// A clock the test moves by hand. Timers still run on the system clock, which these tests do not rely on.
    /// </summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        /// <summary>
        /// The current time.
        /// </summary>
        private DateTimeOffset _now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

        /// <summary>
        /// Returns the current time.
        /// </summary>
        public override DateTimeOffset GetUtcNow() => _now;

        /// <summary>
        /// Moves the clock forward.
        /// </summary>
        /// <param name="by">How far.</param>
        public void Advance(TimeSpan by) => _now += by;
    }
}
