using System.Text.Json.Nodes;
using BlazorA2UI.Catalogs;
using BlazorA2UI.Evaluation;
using BlazorA2UI.Processing;
using Shouldly;
using Xunit;

namespace BlazorA2UI.Tests.Evaluation;

/// <summary>
/// Tests for <see cref="A2UIEvaluator"/> behavior that the conformance suites do not cover.
/// </summary>
public sealed class A2UIEvaluatorTests
{
    /// <summary>
    /// The binding expression the tests evaluate: the asynchronous test function applied to <c>/name</c>.
    /// </summary>
    private const string SlowCall = """{"@call":"slowUpper","args":{"value":{"@path":"/name"}}}""";

    /// <summary>
    /// Proves that a binding over an asynchronous local function shows the settled value on refresh without running the function again.
    /// </summary>
    [Fact]
    public async Task Evaluate_AsyncLocalCallSettled_ReusesResultOnRefreshAsync()
    {
        // Arrange
        SlowFunction slow = new();
        Surface surface = CreateSurface(slow);
        surface.DataModel.Set("/name", "ada");
        A2UIBinding binding = new(surface, JsonNode.Parse(SlowCall), surface.RootContext);
        Task settled = NextChangeAsync(surface);
        binding.Refresh();
        binding.IsPending.ShouldBeTrue();

        // Act
        slow.Release();
        await settled;
        binding.Refresh();
        binding.Refresh();

        // Assert
        binding.IsPending.ShouldBeFalse();
        binding.Value.ShouldNotBeNull().GetValue<string>().ShouldBe("ADA");
        slow.Calls.ShouldBe(1);
    }

    /// <summary>
    /// Proves that a changed argument runs the asynchronous local function again instead of reusing the stored result.
    /// </summary>
    [Fact]
    public async Task Evaluate_AsyncLocalCallInputsChanged_RunsFunctionAgainAsync()
    {
        // Arrange
        SlowFunction slow = new();
        slow.Release();
        Surface surface = CreateSurface(slow);
        surface.DataModel.Set("/name", "ada");
        A2UIBinding binding = new(surface, JsonNode.Parse(SlowCall), surface.RootContext);
        await RefreshUntilSettledAsync(binding);

        // Act
        surface.DataModel.Set("/name", "grace");
        await RefreshUntilSettledAsync(binding);

        // Assert
        binding.Value.ShouldNotBeNull().GetValue<string>().ShouldBe("GRACE");
        slow.Calls.ShouldBe(2);
    }

    /// <summary>
    /// Proves that the error of a settled asynchronous local function reaches the binding on refresh without running the function again.
    /// </summary>
    [Fact]
    public async Task Evaluate_AsyncLocalCallFailed_ReportsStoredErrorAsync()
    {
        // Arrange
        SlowFunction slow = new() { Fail = true };
        Surface surface = CreateSurface(slow);
        surface.DataModel.Set("/name", "ada");
        A2UIBinding binding = new(surface, JsonNode.Parse(SlowCall), surface.RootContext);
        Task settled = NextChangeAsync(surface);
        binding.Refresh();
        binding.IsPending.ShouldBeTrue();
        slow.Release();
        await settled;

        // Act
        binding.Refresh();
        binding.Refresh();

        // Assert
        binding.IsPending.ShouldBeFalse();
        binding.Errors.ShouldContain(e => e.Code == A2UIErrorCodes.ExecutionError);
        slow.Calls.ShouldBe(1);
    }

    /// <summary>
    /// Returns a task that completes the next time the surface reports a change, which is how a settled asynchronous call asks for a refresh.
    /// </summary>
    /// <param name="surface">The surface to watch.</param>
    private static Task NextChangeAsync(Surface surface)
    {
        TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged()
        {
            surface.Changed -= OnChanged;
            changed.TrySetResult();
        }

        surface.Changed += OnChanged;
        return changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Refreshes a binding the way a renderer does: again after each change the surface reports, until it is no longer pending.
    /// The function may also finish before the evaluation checks it, in which case the first refresh already has the value.
    /// </summary>
    /// <param name="binding">The binding to refresh.</param>
    private static async Task RefreshUntilSettledAsync(A2UIBinding binding)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            Task changed = NextChangeAsync(binding.Surface);
            binding.Refresh();
            if (!binding.IsPending)
            {
                return;
            }

            await changed;
        }

        throw new TimeoutException("The binding stayed pending.");
    }

    /// <summary>
    /// Creates a surface whose catalog is the basic catalog plus the asynchronous <c>slowUpper</c> function.
    /// </summary>
    /// <param name="slow">The function implementation.</param>
    private static Surface CreateSurface(SlowFunction slow)
    {
        FunctionDefinition definition = new FunctionDefinition("slowUpper").WithImplementation(slow.InvokeAsync);
        Catalog catalog = BasicCatalog.Create().Extend(functions: [definition]);
        MessageProcessor processor = new([catalog]);
        processor.ProcessOrThrow(JsonNode.Parse($$$"""{"version":"1.0","createSurface":{"surfaceId":"s","catalogId":"{{{catalog.CatalogId}}}"}}"""));
        return processor.Surfaces["s"];
    }

    /// <summary>
    /// A local function that never completes synchronously: it upper-cases its <c>value</c> argument once released.
    /// </summary>
    private sealed class SlowFunction
    {
        /// <summary>
        /// Completes when the function may finish.
        /// </summary>
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Gets the number of invocations so far.
        /// </summary>
        public int Calls { get; private set; }

        /// <summary>
        /// Gets or sets whether the function fails instead of returning a value.
        /// </summary>
        public bool Fail { get; init; }

        /// <summary>
        /// Lets every current and future invocation finish.
        /// </summary>
        public void Release() => _gate.TrySetResult();

        /// <summary>
        /// Runs the function: waits for <see cref="Release"/>, then returns the upper-cased value or throws when <see cref="Fail"/> is set.
        /// </summary>
        /// <exception cref="InvalidOperationException">The function was configured to fail.</exception>
        public async ValueTask<JsonNode?> InvokeAsync(FunctionInvocation invocation)
        {
            Calls++;
            await Task.Yield();
            await _gate.Task;
            if (Fail)
            {
                throw new InvalidOperationException("The test function failed.");
            }

            return JsonValue.Create(A2UIValues.AsString(invocation.Args["value"])?.ToUpperInvariant());
        }
    }
}
