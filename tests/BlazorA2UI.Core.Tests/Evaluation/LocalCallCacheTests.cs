using System.Text.Json.Nodes;
using BlazorA2UI.Evaluation;
using Shouldly;
using Xunit;

namespace BlazorA2UI.Tests.Evaluation;

/// <summary>
/// Tests for the size limit of <see cref="LocalCallCache"/>.
/// </summary>
public sealed class LocalCallCacheTests
{
    /// <summary>
    /// Proves that a full cache of running calls stores nothing more.
    /// </summary>
    [Fact]
    public void Add_FullOfRunningCalls_StoresNothingMore()
    {
        // Arrange
        LocalCallCache cache = new();
        TaskCompletionSource<JsonNode?> running = new();
        for (int i = 0; i < LocalCallCache.MaxEntries; i++)
        {
            cache.Add($"k{i}", running.Task);
        }

        // Act
        cache.Add("extra", running.Task);

        // Assert
        cache.Count.ShouldBe(LocalCallCache.MaxEntries);
        cache.TryGet("extra", out _).ShouldBeFalse();
    }

    /// <summary>
    /// Proves that a full cache drops its settled calls to make room for a new one.
    /// </summary>
    [Fact]
    public void Add_FullWithSettledCalls_DropsSettledCalls()
    {
        // Arrange
        LocalCallCache cache = new();
        TaskCompletionSource<JsonNode?> running = new();
        cache.Add("running", running.Task);
        for (int i = 1; i < LocalCallCache.MaxEntries; i++)
        {
            cache.Add($"k{i}", Task.FromResult<JsonNode?>(i));
        }

        // Act
        cache.Add("extra", running.Task);

        // Assert
        cache.Count.ShouldBe(2);
        cache.TryGet("running", out _).ShouldBeTrue();
        cache.TryGet("extra", out _).ShouldBeTrue();
    }
}
