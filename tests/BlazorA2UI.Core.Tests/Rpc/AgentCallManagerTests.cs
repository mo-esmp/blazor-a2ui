using System.Collections.Concurrent;
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
    /// The id of the basic catalog the tests create surfaces with.
    /// </summary>
    private static readonly string s_basicId = BasicCatalog.CatalogId;

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

    /// <summary>
    /// Proves that a timer of a settled call never touches a later call that reuses its id: the second call
    /// still completes with its own answer after the first call's timeout has passed.
    /// </summary>
    [Fact]
    public async Task CallAgentFunctionAsync_IdReusedAfterAnswer_OldTimerLeavesNewCallAloneAsync()
    {
        // Arrange
        MessageProcessor processor = Create(out _, "s");
        Task<JsonNode?> first = processor.CallAgentFunctionAsync("s", s_basicId, "lookup", null, new A2UIAgentCallOptions { FunctionCallId = "fc", Timeout = TimeSpan.FromMilliseconds(50) });
        processor.ProcessOrThrow(Response("fc", 1));
        (await first)?.GetValue<int>().ShouldBe(1);
        Task<JsonNode?> second = processor.CallAgentFunctionAsync("s", s_basicId, "lookup", null, new A2UIAgentCallOptions { FunctionCallId = "fc", Timeout = Timeout.InfiniteTimeSpan });

        // Act
        await Task.Delay(200);
        processor.ProcessOrThrow(Response("fc", 2));

        // Assert
        (await second.WaitAsync(TimeSpan.FromSeconds(5)))?.GetValue<int>().ShouldBe(2);
        processor.IsAgentCallPending("fc").ShouldBeFalse();
    }

    /// <summary>
    /// Proves that a token cancelled before the call fails it with CANCELLED without sending anything or reserving the id.
    /// </summary>
    [Fact]
    public async Task CallAgentFunctionAsync_TokenAlreadyCancelled_SendsNothingAsync()
    {
        // Arrange
        MessageProcessor processor = Create(out ConcurrentQueue<A2UIOutboundMessage> sent, "s");
        using CancellationTokenSource cancel = new();
        await cancel.CancelAsync();

        // Act
        A2UIException error = await Should.ThrowAsync<A2UIException>(() =>
            processor.CallAgentFunctionAsync("s", s_basicId, "lookup", null, new A2UIAgentCallOptions { FunctionCallId = "fc" }, cancel.Token));

        // Assert
        error.Code.ShouldBe(A2UIErrorCodes.Cancelled);
        sent.ShouldBeEmpty();
        processor.IsAgentCallPending("fc").ShouldBeFalse();
    }

    /// <summary>
    /// Proves that an unsupported timeout is refused before anything is sent or reserved, so a retry with a valid
    /// timeout and the same id is accepted.
    /// </summary>
    /// <param name="milliseconds">The timeout, in milliseconds; <see cref="double.MaxValue"/> stands for <see cref="TimeSpan.MaxValue"/>.</param>
    [Theory]
    [InlineData(double.MaxValue)]
    [InlineData(0d)]
    [InlineData(-5d)]
    public void CallAgentFunctionAsync_InvalidTimeout_ThrowsAndReservesNothing(double milliseconds)
    {
        // Arrange
        MessageProcessor processor = Create(out ConcurrentQueue<A2UIOutboundMessage> sent, "s");
        TimeSpan timeout = milliseconds == double.MaxValue ? TimeSpan.MaxValue : TimeSpan.FromMilliseconds(milliseconds);

        // Act
        Action call = () => processor.CallAgentFunctionAsync("s", s_basicId, "lookup", null, new A2UIAgentCallOptions { FunctionCallId = "fc", Timeout = timeout });

        // Assert
        call.ShouldThrow<ArgumentOutOfRangeException>();
        sent.ShouldBeEmpty();
        processor.IsAgentCallPending("fc").ShouldBeFalse();
        Task<JsonNode?> retry = processor.CallAgentFunctionAsync("s", s_basicId, "lookup", null, new A2UIAgentCallOptions { FunctionCallId = "fc", Timeout = TimeSpan.FromMinutes(1) });
        retry.IsFaulted.ShouldBeFalse();
        processor.Dispose();
    }

    /// <summary>
    /// Proves that concurrent calls with one id, on one surface or several, reserve it exactly once: one call is
    /// sent and all the others fail with DUPLICATE.
    /// </summary>
    [Fact]
    public async Task CallAgentFunctionAsync_ConcurrentSameId_ReservesOnceAsync()
    {
        // Arrange
        MessageProcessor processor = Create(out ConcurrentQueue<A2UIOutboundMessage> sent, "s1", "s2");
        using Barrier start = new(16);

        // Act
        Task<JsonNode?>[] calls = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => OnOwnThread(() =>
        {
            start.SignalAndWait();
            return processor.CallAgentFunctionAsync(i % 2 == 0 ? "s1" : "s2", s_basicId, "lookup", null, new A2UIAgentCallOptions { FunctionCallId = "fc" });
        })));

        // Assert
        calls.Count(c => c.IsFaulted && c.Exception?.GetBaseException() is A2UIException { Code: A2UIErrorCodes.Duplicate }).ShouldBe(15);
        Task<JsonNode?> winner = calls.Where(c => !c.IsFaulted).ShouldHaveSingleItem();
        sent.Count.ShouldBe(1);
        processor.ProcessOrThrow(Response("fc", 7));
        (await winner.WaitAsync(TimeSpan.FromSeconds(5)))?.GetValue<int>().ShouldBe(7);
    }

    /// <summary>
    /// Proves that disposal racing with new calls leaves no call pending: every call either is refused with
    /// DISPOSED or is cancelled by the disposal.
    /// </summary>
    [Fact]
    public async Task Dispose_ConcurrentWithCalls_LeavesNoCallPendingAsync()
    {
        // Arrange
        MessageProcessor processor = new([BasicCatalog.Create()], new A2UIProcessorOptions { AgentCallTimeout = Timeout.InfiniteTimeSpan });
        processor.Outbound = _ => ValueTask.CompletedTask;
        processor.ProcessOrThrow(JsonNode.Parse($$$"""{"version":"1.0","createSurface":{"surfaceId":"s","catalogId":"{{{s_basicId}}}"}}"""));
        using Barrier start = new(9);
        Task<Task<JsonNode?>>[] callers = [.. Enumerable.Range(0, 8).Select(i => OnOwnThread(() =>
        {
            start.SignalAndWait();
            return processor.CallAgentFunctionAsync("s", s_basicId, "lookup", null, new A2UIAgentCallOptions { FunctionCallId = $"fc-{i}" });
        }))];

        // Act
        start.SignalAndWait();
        processor.Dispose();
        Task<JsonNode?>[] calls = await Task.WhenAll(callers);

        // Assert
        foreach (Task<JsonNode?> call in calls)
        {
            A2UIException error = await Should.ThrowAsync<A2UIException>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
            error.Code.ShouldBeOneOf(A2UIErrorCodes.Disposed, A2UIErrorCodes.Cancelled);
        }
    }

    /// <summary>
    /// Creates a processor with an infinite default timeout, an outbound handler that records every message,
    /// and one surface per id.
    /// </summary>
    /// <param name="sent">Receives the messages the processor sends.</param>
    /// <param name="surfaceIds">The surfaces to create.</param>
    private static MessageProcessor Create(out ConcurrentQueue<A2UIOutboundMessage> sent, params string[] surfaceIds)
    {
        MessageProcessor processor = new([BasicCatalog.Create()], new A2UIProcessorOptions { AgentCallTimeout = Timeout.InfiniteTimeSpan });
        ConcurrentQueue<A2UIOutboundMessage> queue = new();
        processor.Outbound = message =>
        {
            queue.Enqueue(message);
            return ValueTask.CompletedTask;
        };
        foreach (string surfaceId in surfaceIds)
        {
            processor.ProcessOrThrow(JsonNode.Parse($$$"""{"version":"1.0","createSurface":{"surfaceId":"{{{surfaceId}}}","catalogId":"{{{s_basicId}}}"}}"""));
        }

        sent = queue;
        return processor;
    }

    /// <summary>
    /// Builds the agent's answer to a call.
    /// </summary>
    /// <param name="functionCallId">The call's id.</param>
    /// <param name="value">The answer.</param>
    private static JsonNode? Response(string functionCallId, int value) =>
        JsonNode.Parse($$$"""{"version":"1.0","agentFunctionResponse":{"functionCallId":"{{{functionCallId}}}","value":{{{value}}}}}""");

    /// <summary>
    /// Starts a call on a dedicated thread and returns its task without unwrapping it, so a barrier can line the calls up.
    /// </summary>
    /// <param name="start">Makes the call.</param>
    private static Task<Task<JsonNode?>> OnOwnThread(Func<Task<JsonNode?>> start) =>
        Task.Factory.StartNew(start, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
