namespace OpenFlightDisplay.App.Tests;

using OpenFlightDisplay.App.Services;
using Xunit;

/// <summary>
/// The guard that stops an <c>async void</c> event handler killing the process.
/// </summary>
/// <remarks>
/// This exists because it already happened: a map tile race threw on the UI
/// thread and the application vanished, reported by the user as "it froze on
/// launch". These tests pin the behaviour that must not regress — a failure is
/// contained <b>and</b> reported, never one without the other.
/// </remarks>
public class SafeHandlerTests
{
    [Fact]
    public async Task A_successful_handler_reports_nothing()
    {
        var reported = new List<string>();
        bool ran = false;

        await SafeHandler.RunAsync(
            () => { ran = true; return Task.CompletedTask; },
            reported.Add);

        Assert.True(ran);
        Assert.Empty(reported);
    }

    [Fact]
    public async Task A_throwing_handler_does_not_propagate()
    {
        // If this ever rethrows, the caller is an async void event handler and
        // the process dies.
        var reported = new List<string>();

        await SafeHandler.RunAsync(
            () => throw new InvalidOperationException("boom"),
            reported.Add);

        Assert.Single(reported);
    }

    [Fact]
    public async Task The_failure_message_reaches_the_user()
    {
        // Containing the exception silently would be worse than crashing: the
        // button would simply do nothing, forever, with no explanation.
        var reported = new List<string>();

        await SafeHandler.RunAsync(
            () => throw new InvalidOperationException("the database is locked"),
            reported.Add);

        Assert.Contains("the database is locked", reported[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_after_an_await_is_still_caught()
    {
        // The case that actually bit: the exception was thrown on resumption
        // after an await, not synchronously.
        var reported = new List<string>();

        await SafeHandler.RunAsync(
            async () =>
            {
                await Task.Yield();
                throw new InvalidOperationException("after the await");
            },
            reported.Add);

        Assert.Contains("after the await", reported[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_is_not_reported_as_a_failure()
    {
        // Shutdown, or an operation superseded by a newer one. Showing the user
        // an error for either would be noise.
        var reported = new List<string>();

        await SafeHandler.RunAsync(
            () => throw new OperationCanceledException(),
            reported.Add);

        Assert.Empty(reported);
    }

    [Fact]
    public async Task A_reporter_that_itself_throws_does_not_escape()
    {
        // The reporter touches UI. During teardown it can throw, and that
        // exception would be exactly the unhandled async void escape this class
        // exists to prevent.
        await SafeHandler.RunAsync(
            () => throw new InvalidOperationException("original"),
            _ => throw new InvalidOperationException("the window is gone"));
    }

    [Fact]
    public async Task A_null_action_is_a_programming_error_not_a_silent_no_op()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => SafeHandler.RunAsync(null!, _ => { }));
    }
}
