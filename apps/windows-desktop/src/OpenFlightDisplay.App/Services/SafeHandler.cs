namespace OpenFlightDisplay.App.Services;

using Microsoft.UI.Dispatching;

/// <summary>
/// Runs an asynchronous event handler without letting it kill the process.
/// </summary>
/// <remarks>
/// <para>
/// XAML event handlers must be <c>async void</c>, and an exception escaping one
/// on the UI thread terminates the application outright — no dialog, no message,
/// and nothing in the event log beyond a stowed-exception code. It presents to
/// the user as "it froze on launch", which is precisely how the map tile race of
/// 2026-08-01 was reported.
/// </para>
/// <para>
/// This is deliberately <b>not</b> a blanket catch that lets the application
/// limp on pretending nothing happened. The exception is reported through a
/// caller-supplied channel, which in this application means the same status text
/// the user is already reading. A handler that fails says so; it just does not
/// take the window with it.
/// </para>
/// <para>
/// Genuine programmer errors still surface — they simply surface as a visible
/// message instead of a vanished process, which is the whole point of the
/// project's no-silent-failure rule.
/// </para>
/// </remarks>
public static class SafeHandler
{
    /// <summary>
    /// Awaits <paramref name="action"/>, reporting any failure.
    /// </summary>
    /// <param name="report">
    /// Shows the failure to the user. Called on the same thread the handler was
    /// running on, which for a XAML event is the UI thread.
    /// </param>
    public static async Task RunAsync(Func<Task> action, Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(report);

        try
        {
            await action().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Shutdown, or a superseded operation. Not a failure worth showing.
        }
#pragma warning disable CA1031 // The entire purpose of this method.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            TryReport(report, ex);
        }
    }

    /// <summary>
    /// Reports the failure, and never throws while doing so.
    /// </summary>
    /// <remarks>
    /// The reporter touches UI. If the window is already tearing down that will
    /// itself throw, and an exception here would be exactly the unhandled
    /// <c>async void</c> escape this class exists to prevent.
    /// </remarks>
    private static void TryReport(Action<string> report, Exception ex)
    {
        try
        {
            report($"Something went wrong: {ex.Message}");
        }
#pragma warning disable CA1031 // Reporting a failure must not cause one.
        catch (Exception)
#pragma warning restore CA1031
        {
            // The window is going away. App.UnhandledException still records it.
        }
    }

    /// <summary>
    /// Marshals <paramref name="action"/> onto the UI thread, safely.
    /// </summary>
    /// <remarks>
    /// For handlers raised from background threads — the feed and the tracking
    /// loop both do this. A failed enqueue means shutdown and is not an error.
    /// </remarks>
    public static void Post(DispatcherQueue dispatcher, Action action, Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(report);

        dispatcher.TryEnqueue(() =>
        {
            try
            {
                action();
            }
#pragma warning disable CA1031 // As above.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                TryReport(report, ex);
            }
        });
    }
}
