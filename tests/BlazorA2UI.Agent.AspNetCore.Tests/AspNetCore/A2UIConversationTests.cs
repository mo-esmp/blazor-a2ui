using BlazorA2UI.Catalogs;
using BlazorA2UI.Processing;

namespace BlazorA2UI.Tests.AspNetCore;

/// <summary>
/// Tests for <see cref="A2UIConversation"/>: how its sends keep the mirror in step with what the client receives.
/// </summary>
public sealed class A2UIConversationTests
{
    /// <summary>
    /// The id of the conversations under test.
    /// </summary>
    private const string Id = "abcdefghijklmnopqrstuv";

    /// <summary>
    /// The id of the catalog with a renderer function.
    /// </summary>
    private const string ToolsId = "urn:tools";

    /// <summary>
    /// Proves that a <c>deleteSurface</c> whose write fails is taken back, so the surface can still be updated.
    /// </summary>
    [Fact]
    public async Task SendAsync_DeleteWriteFails_KeepsSurface()
    {
        // Arrange
        using A2UIConversation conversation = new(Id, null, Options());
        A2UIStreamWriter good = new(new MemoryStream(), eventStream: false);
        A2UIStreamWriter broken = new(new GatedStream { Fails = true }, eventStream: false);
        (await conversation.SendAsync(A2UIMessages.CreateSurface("main", new Text { Content = "Hi" }), good, CancellationToken.None)).ShouldBeNull();
        await Should.ThrowAsync<IOException>(() => conversation.SendAsync(A2UIMessages.DeleteSurface("main"), broken, CancellationToken.None));

        // Act
        A2UIErrorReport? error = await conversation.SendAsync(A2UIMessages.UpdateDataModel("main", "/name", "Ada"), good, CancellationToken.None);

        // Assert
        error.ShouldBeNull();
    }

    /// <summary>
    /// Proves that a renderer function call that times out while an earlier frame is still being written is never
    /// written.
    /// </summary>
    [Fact]
    public async Task SendCallAsync_TimesOutWhileWaiting_IsNotWritten()
    {
        // Arrange
        using A2UIConversation conversation = new(Id, null, Options());
        GatedStream stream = new();
        A2UIStreamWriter writer = new(stream, eventStream: false);
        Task first = conversation.SendAsync(A2UIMessages.CreateSurface("main", new Text { Content = "Hi" }), writer, CancellationToken.None);
        await stream.Writing.Task;
        A2UIRendererFunctionCall call = conversation.RendererCalls.Call(new A2UICall("getCity") { CatalogId = ToolsId }, timeout: TimeSpan.FromMilliseconds(50));

        // Act
        A2UIErrorReport? error = await conversation.SendCallAsync(call, null, writer, CancellationToken.None);
        stream.Release.SetResult();
        await first;

        // Assert
        error.ShouldBeNull();
        (await Should.ThrowAsync<A2UIException>(call.Result)).Code.ShouldBe(A2UIErrorCodes.Timeout);
        stream.Frames.ShouldBe(1);
    }

    /// <summary>
    /// Proves that the id is kept out of logs: the log id is a short fingerprint, not the id.
    /// </summary>
    [Fact]
    public void LogId_IsFingerprintOfId()
    {
        // Arrange
        using A2UIConversation conversation = new(Id, null, Options());

        // Act
        string logId = conversation.LogId;

        // Assert
        logId.Length.ShouldBe(12);
        conversation.Id.ShouldNotContain(logId);
    }

    /// <summary>
    /// Builds options with the basic catalog and a catalog with a renderer function, <c>getCity</c>.
    /// </summary>
    private static A2UIAgentEndpointOptions Options()
    {
        A2UIAgentEndpointOptions options = new();
        options.Catalogs.Add(BasicCatalog.Create());
        options.Catalogs.Add(new Catalog(
            ToolsId,
            [],
            [new FunctionDefinition("getCity") { AllowedCallers = AllowedCallers.RendererOrAgent, ReturnType = "string" }],
            A2UIProtocol.Version));
        return options;
    }

    /// <summary>
    /// A response body that holds the first write until released, or fails every write.
    /// </summary>
    private sealed class GatedStream : Stream
    {
        /// <summary>
        /// Gets or sets whether every write fails.
        /// </summary>
        public bool Fails { get; init; }

        /// <summary>
        /// Gets a task that completes when the first write starts.
        /// </summary>
        public TaskCompletionSource Writing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Gets the source that lets the first write finish.
        /// </summary>
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Gets how many frames were written.
        /// </summary>
        public int Frames { get; private set; }

        /// <inheritdoc />
        public override bool CanRead => false;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => true;

        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        /// <inheritdoc />
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Fails)
            {
                throw new IOException("The client went away.");
            }

            Frames++;
            if (Frames == 1)
            {
                Writing.SetResult();
                await Release.Task;
            }
        }

        /// <inheritdoc />
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <inheritdoc />
        public override void Flush()
        {
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
