namespace BlazorA2UI.Tests.SampleAgent;

/// <summary>
/// Tests for <see cref="BookingAgent"/> that need no HTTP.
/// </summary>
public sealed class BookingAgentTests
{
    /// <summary>
    /// Proves that the repair message stays within its budget however many problems there are, counts the ones left
    /// out, and always ends with the instruction.
    /// </summary>
    [Fact]
    public void BuildRepairPrompt_ManyProblems_StaysWithinBudget()
    {
        // Arrange
        List<A2UIErrorPart> problems = [.. Enumerable.Range(0, 500).Select(i => new A2UIErrorPart($"Problem {i}: " + new string('x', 200)))];

        // Act
        string prompt = BookingAgent.BuildRepairPrompt(problems);

        // Assert
        prompt.Length.ShouldBeLessThanOrEqualTo(BookingAgent.MaxRepairPromptLength);
        prompt.ShouldStartWith(BookingAgent.RepairPrompt);
        prompt.ShouldContain("Problem 0: ");
        prompt.ShouldContain("more problems.");
        prompt.ShouldEndWith("Do not repeat the messages that were sent.");
    }

    /// <summary>
    /// Proves that a long problem is shortened, so one problem cannot fill the message.
    /// </summary>
    [Fact]
    public void BuildRepairPrompt_LongProblem_IsShortened()
    {
        // Arrange
        A2UIErrorPart problem = new(new string('x', 10_000));

        // Act
        string prompt = BookingAgent.BuildRepairPrompt([problem]);

        // Assert
        prompt.Length.ShouldBeLessThan(1000);
        prompt.ShouldNotContain("more problems.");
    }
}
