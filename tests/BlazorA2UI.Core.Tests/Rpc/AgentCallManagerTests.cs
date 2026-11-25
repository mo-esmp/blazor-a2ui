using System.Text.Json.Nodes;
using BlazorA2UI.Catalogs;
using BlazorA2UI.Processing;
using BlazorA2UI.Rpc;
using Shouldly;
using Xunit;

namespace BlazorA2UI.Tests.Rpc;

/// <summary>
/// Tests for <see cref="AgentCallManager"/> behavior that the conformance suites do not cover.
/// </summary>
public sealed class AgentCallManagerTests
{
    /// <summary>
    /// Proves that cancelling an unanswered action call frees its pending-call slot, so the next call can be sent.
    /// </summary>
    [Fact]
    public async Task CallAsync_CancelledCall_ReleasesPendingSlotAsync()
    {
        // Arrange
        MessageProcessor processor = new([BasicCatalog.Create()], new A2UIProcessorOptions { MaxPendingAgentCalls = 1, AgentCallTimeout = Timeout.InfiniteTimeSpan });
        List<A2UIOutboundMessage> sent = [];
        processor.Outbound = m =>
        {
            lock (sent)
            {
                sent.Add(m);
            }

            return ValueTask.CompletedTask;
        };
        processor.ProcessOrThrow(JsonNode.Parse($$$"""{"version":"1.0","createSurface":{"surfaceId":"s","catalogId":"{{{BasicCatalog.CatalogId}}}"}}"""));
        Surface surface = processor.Surfaces["s"];
        using CancellationTokenSource cancel = new();
        Task<JsonNode?> first = surface.AgentCalls.CallAsync(BasicCatalog.CatalogId, "lookup", new JsonObject(), cancel.Token);

        // Act
        await cancel.CancelAsync();
        Func<Task> waitFirst = () => first;
        await waitFirst.ShouldThrowAsync<A2UIException>();
        Task<JsonNode?> second = surface.AgentCalls.CallAsync(BasicCatalog.CatalogId, "lookup", new JsonObject(), CancellationToken.None);
        await processor.WhenIdleAsync();

        // Assert
        second.IsFaulted.ShouldBeFalse();
        sent.Count(m => m.Kind == "callAgentFunction").ShouldBe(2);
        surface.AgentCalls.CancelAll("Test finished.");
        Func<Task> waitSecond = () => second;
        await waitSecond.ShouldThrowAsync<A2UIException>();
    }
}
